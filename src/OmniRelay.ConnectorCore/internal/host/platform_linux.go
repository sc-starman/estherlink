//go:build linux

package host

import (
	"bufio"
	"fmt"
	"os"
	"strings"
)

func VerifySupportedPlatform() error {
	file, err := os.Open("/etc/os-release")
	if err != nil {
		return fmt.Errorf("read /etc/os-release: %w", err)
	}
	defer file.Close()
	values := make(map[string]string)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		key, value, ok := strings.Cut(scanner.Text(), "=")
		if !ok {
			continue
		}
		values[key] = strings.Trim(strings.TrimSpace(value), `"'`)
	}
	if err := scanner.Err(); err != nil {
		return err
	}
	id := strings.ToLower(values["ID"])
	idLike := strings.ToLower(values["ID_LIKE"])
	if id == "ubuntu" || id == "debian" || strings.Contains(idLike, "debian") || strings.Contains(idLike, "ubuntu") {
		return nil
	}
	return fmt.Errorf("unsupported Linux distribution %q; Ubuntu/Debian systemd is required", id)
}
