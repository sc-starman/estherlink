package firewall

import (
	"context"
	"strings"
	"testing"
)

type recordedRunner struct {
	commands []string
}

func (r *recordedRunner) Run(_ context.Context, name string, args ...string) error {
	r.commands = append(r.commands, name+" "+strings.Join(args, " "))
	if strings.Contains(strings.Join(args, " "), " -C ") {
		return context.DeadlineExceeded
	}
	return nil
}

func TestApplyUsesOnlyRelayOwnedChains(t *testing.T) {
	runner := &recordedRunner{}
	result, err := Apply(context.Background(), runner, Desired{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d", Interface: "omnie4ccc28", RedirectPort: 46087,
	})
	if err != nil {
		t.Fatal(err)
	}
	if !result.Applied || len(result.ChainID) != 10 {
		t.Fatalf("unexpected result: %+v", result)
	}
	combined := strings.Join(runner.commands, "\n")
	if strings.Contains(combined, "iptables -t nat -F PREROUTING") || strings.Contains(combined, "iptables -t filter -F FORWARD") {
		t.Fatalf("administrator-owned chain would be flushed: %s", combined)
	}
	for _, expected := range []string{"OMNIREDIR" + result.ChainID, "OMNIREJ" + result.ChainID} {
		if !strings.Contains(combined, expected) {
			t.Fatalf("managed chain missing %q: %s", expected, combined)
		}
	}
}
