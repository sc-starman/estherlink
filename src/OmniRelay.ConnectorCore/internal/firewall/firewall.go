package firewall

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os/exec"
	"strconv"
)

type Desired struct {
	RelayID          string
	Interface        string
	RedirectPort     int
	DNSRedirect      bool
	DNSListenAddress string
}

type Result struct {
	Applied bool   `json:"applied"`
	ChainID string `json:"chainId"`
}

type Runner interface {
	Run(context.Context, string, ...string) error
}

type OSRunner struct{}

func (OSRunner) Run(ctx context.Context, name string, args ...string) error {
	output, err := exec.CommandContext(ctx, name, args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %w: %s", name, err, output)
	}
	return nil
}

func Apply(ctx context.Context, runner Runner, desired Desired) (Result, error) {
	if err := validate(desired); err != nil {
		return Result{}, err
	}
	chainID := shortID(desired.RelayID)
	redirect := "OMNIREDIR" + chainID
	reject := "OMNIREJ" + chainID
	if err := reconcileChain(ctx, runner, "nat", redirect, [][]string{
		{"-d", "127.0.0.0/8", "-j", "RETURN"},
		{"-p", "tcp", "-j", "REDIRECT", "--to-ports", strconv.Itoa(desired.RedirectPort)},
	}); err != nil {
		return Result{}, err
	}
	if err := ensureJump(ctx, runner, "nat", "PREROUTING", []string{"-i", desired.Interface, "-p", "tcp", "-j", redirect}); err != nil {
		return Result{}, err
	}
	if err := reconcileChain(ctx, runner, "filter", reject, [][]string{
		{"-p", "udp", "--dport", "443", "-j", "REJECT", "--reject-with", "icmp-port-unreachable"},
	}); err != nil {
		return Result{}, err
	}
	if err := ensureJump(ctx, runner, "filter", "FORWARD", []string{"-i", desired.Interface, "-p", "udp", "--dport", "443", "-j", reject}); err != nil {
		return Result{}, err
	}
	if desired.DNSRedirect {
		dns := "OMNIDNS" + chainID
		if err := reconcileChain(ctx, runner, "nat", dns, [][]string{
			{"-p", "udp", "--dport", "53", "-j", "REDIRECT", "--to-ports", "53"},
			{"-p", "tcp", "--dport", "53", "-j", "REDIRECT", "--to-ports", "53"},
		}); err != nil {
			return Result{}, err
		}
		if err := ensureJump(ctx, runner, "nat", "PREROUTING", []string{"-i", desired.Interface, "-j", dns}); err != nil {
			return Result{}, err
		}
	}
	return Result{Applied: true, ChainID: chainID}, nil
}

func Cleanup(ctx context.Context, runner Runner, desired Desired) error {
	if err := validate(desired); err != nil {
		return err
	}
	chainID := shortID(desired.RelayID)
	for _, item := range []struct {
		table string
		base  string
		jump  []string
		chain string
	}{
		{"nat", "PREROUTING", []string{"-i", desired.Interface, "-p", "tcp", "-j", "OMNIREDIR" + chainID}, "OMNIREDIR" + chainID},
		{"filter", "FORWARD", []string{"-i", desired.Interface, "-p", "udp", "--dport", "443", "-j", "OMNIREJ" + chainID}, "OMNIREJ" + chainID},
		{"nat", "PREROUTING", []string{"-i", desired.Interface, "-j", "OMNIDNS" + chainID}, "OMNIDNS" + chainID},
	} {
		_ = runner.Run(ctx, "iptables", append([]string{"-t", item.table, "-D", item.base}, item.jump...)...)
		_ = runner.Run(ctx, "iptables", "-t", item.table, "-F", item.chain)
		_ = runner.Run(ctx, "iptables", "-t", item.table, "-X", item.chain)
	}
	return nil
}

func reconcileChain(ctx context.Context, runner Runner, table string, chain string, rules [][]string) error {
	_ = runner.Run(ctx, "iptables", "-t", table, "-N", chain)
	if err := runner.Run(ctx, "iptables", "-t", table, "-F", chain); err != nil {
		return err
	}
	for _, rule := range rules {
		if err := runner.Run(ctx, "iptables", append([]string{"-t", table, "-A", chain}, rule...)...); err != nil {
			return err
		}
	}
	return nil
}

func ensureJump(ctx context.Context, runner Runner, table string, chain string, rule []string) error {
	check := append([]string{"-t", table, "-C", chain}, rule...)
	if err := runner.Run(ctx, "iptables", check...); err == nil {
		return nil
	}
	return runner.Run(ctx, "iptables", append([]string{"-t", table, "-A", chain}, rule...)...)
}

func validate(desired Desired) error {
	if desired.RelayID == "" || desired.Interface == "" || desired.RedirectPort < 1 || desired.RedirectPort > 65535 {
		return fmt.Errorf("invalid managed firewall desired state")
	}
	return nil
}

func shortID(relayID string) string {
	hash := sha256.Sum256([]byte(relayID))
	return hex.EncodeToString(hash[:5])
}
