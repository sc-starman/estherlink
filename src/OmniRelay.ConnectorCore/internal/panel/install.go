package panel

import (
	"archive/tar"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

type InstallResult struct {
	Changed     bool   `json:"changed"`
	ReleasePath string `json:"releasePath"`
	CurrentPath string `json:"currentPath"`
	SHA256      string `json:"sha256"`
}

func InstallArtifact(artifactPath string, expectedSHA256 string, appRoot string) (InstallResult, error) {
	actualSHA256, err := fileSHA256(artifactPath)
	if err != nil {
		return InstallResult{}, err
	}
	if !strings.EqualFold(strings.TrimSpace(expectedSHA256), actualSHA256) {
		return InstallResult{}, fmt.Errorf("OmniPanel artifact SHA256 mismatch")
	}
	releaseRoot := filepath.Join(appRoot, "releases")
	releasePath := filepath.Join(releaseRoot, actualSHA256[:16])
	currentPath := filepath.Join(appRoot, "current")
	if info, err := os.Stat(filepath.Join(releasePath, "server.js")); err == nil && info.Mode().IsRegular() {
		changed, err := activateSymlink(currentPath, releasePath)
		return InstallResult{Changed: changed, ReleasePath: releasePath, CurrentPath: currentPath, SHA256: actualSHA256}, err
	}
	if err := os.MkdirAll(releaseRoot, 0o755); err != nil {
		return InstallResult{}, err
	}
	tempPath, err := os.MkdirTemp(releaseRoot, ".extract-*")
	if err != nil {
		return InstallResult{}, err
	}
	defer safeRemoveAll(releaseRoot, tempPath)
	if err := extractTarGzip(artifactPath, tempPath); err != nil {
		return InstallResult{}, err
	}
	serverPath, err := locateServerRoot(tempPath)
	if err != nil {
		return InstallResult{}, err
	}
	if serverPath != tempPath {
		normalized := filepath.Join(releaseRoot, ".normalized-"+actualSHA256[:16])
		_ = safeRemoveAll(releaseRoot, normalized)
		if err := os.Rename(serverPath, normalized); err != nil {
			return InstallResult{}, err
		}
		tempPath = normalized
		defer safeRemoveAll(releaseRoot, tempPath)
	}
	if err := os.Rename(tempPath, releasePath); err != nil {
		if !errors.Is(err, os.ErrExist) {
			return InstallResult{}, err
		}
	}
	changed, err := activateSymlink(currentPath, releasePath)
	return InstallResult{Changed: changed, ReleasePath: releasePath, CurrentPath: currentPath, SHA256: actualSHA256}, err
}

func ValidateCurrent(appRoot string) error {
	info, err := os.Stat(filepath.Join(appRoot, "current", "server.js"))
	if err != nil {
		return fmt.Errorf("OmniPanel current release is invalid: %w", err)
	}
	if !info.Mode().IsRegular() || info.Size() == 0 {
		return fmt.Errorf("OmniPanel current server.js is not a non-empty regular file")
	}
	return nil
}

func extractTarGzip(artifactPath string, destination string) error {
	file, err := os.Open(artifactPath)
	if err != nil {
		return err
	}
	defer file.Close()
	gzipReader, err := gzip.NewReader(file)
	if err != nil {
		return err
	}
	defer gzipReader.Close()
	reader := tar.NewReader(gzipReader)
	for {
		header, err := reader.Next()
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return err
		}
		name := filepath.Clean(filepath.FromSlash(header.Name))
		target := filepath.Join(destination, name)
		if !pathWithin(destination, target) {
			return fmt.Errorf("OmniPanel artifact contains unsafe path %q", header.Name)
		}
		switch header.Typeflag {
		case tar.TypeDir:
			if err := os.MkdirAll(target, 0o755); err != nil {
				return err
			}
		case tar.TypeReg, tar.TypeRegA:
			if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
				return err
			}
			mode := os.FileMode(header.Mode).Perm()
			if mode&0o111 != 0 {
				mode = 0o755
			} else {
				mode = 0o644
			}
			output, err := os.OpenFile(target, os.O_CREATE|os.O_EXCL|os.O_WRONLY, mode)
			if err != nil {
				return err
			}
			_, copyErr := io.Copy(output, reader)
			closeErr := output.Close()
			if copyErr != nil {
				return copyErr
			}
			if closeErr != nil {
				return closeErr
			}
		default:
			return fmt.Errorf("OmniPanel artifact contains unsupported entry %q", header.Name)
		}
	}
}

func locateServerRoot(root string) (string, error) {
	if info, err := os.Stat(filepath.Join(root, "server.js")); err == nil && info.Mode().IsRegular() {
		return root, nil
	}
	entries, err := os.ReadDir(root)
	if err != nil {
		return "", err
	}
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		candidate := filepath.Join(root, entry.Name())
		if info, err := os.Stat(filepath.Join(candidate, "server.js")); err == nil && info.Mode().IsRegular() {
			return candidate, nil
		}
	}
	return "", fmt.Errorf("OmniPanel artifact is missing server.js")
}

func activateSymlink(currentPath string, releasePath string) (bool, error) {
	if target, err := os.Readlink(currentPath); err == nil {
		resolvedTarget := target
		if !filepath.IsAbs(resolvedTarget) {
			resolvedTarget = filepath.Join(filepath.Dir(currentPath), resolvedTarget)
		}
		if absoluteTarget, err := filepath.Abs(resolvedTarget); err == nil {
			absoluteRelease, _ := filepath.Abs(releasePath)
			if absoluteTarget == absoluteRelease {
				return false, nil
			}
		}
	}
	if err := os.MkdirAll(filepath.Dir(currentPath), 0o755); err != nil {
		return false, err
	}
	temp := currentPath + ".new"
	_ = os.Remove(temp)
	if err := os.Symlink(releasePath, temp); err != nil {
		return false, err
	}
	if err := os.Rename(temp, currentPath); err != nil {
		_ = os.Remove(temp)
		return false, err
	}
	return true, nil
}

func fileSHA256(path string) (string, error) {
	file, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer file.Close()
	hash := sha256.New()
	if _, err := io.Copy(hash, file); err != nil {
		return "", err
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}

func safeRemoveAll(root string, target string) error {
	if !pathWithin(root, target) || filepath.Clean(root) == filepath.Clean(target) {
		return fmt.Errorf("refusing unsafe recursive removal of %s", target)
	}
	return os.RemoveAll(target)
}

func pathWithin(root string, target string) bool {
	relative, err := filepath.Rel(filepath.Clean(root), filepath.Clean(target))
	return err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator))
}
