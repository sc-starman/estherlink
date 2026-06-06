package host

import (
	"context"
	"fmt"
	"regexp"
	"sort"
)

type PackageManager interface {
	Ensure(context.Context, []string) error
}

var packageNamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9+.-]*$`)

func ValidatePackageNames(packages []string) ([]string, error) {
	seen := make(map[string]struct{}, len(packages))
	result := make([]string, 0, len(packages))
	for _, name := range packages {
		if !packageNamePattern.MatchString(name) {
			return nil, fmt.Errorf("unsafe package name %q", name)
		}
		if _, exists := seen[name]; exists {
			continue
		}
		seen[name] = struct{}{}
		result = append(result, name)
	}
	sort.Strings(result)
	return result, nil
}
