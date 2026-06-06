package host

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"path/filepath"
)

func WriteFileAtomic(path string, content []byte, mode os.FileMode) (bool, error) {
	if current, err := os.ReadFile(path); err == nil && bytes.Equal(current, content) {
		info, statErr := os.Stat(path)
		if statErr != nil {
			return false, statErr
		}
		if fileModeMatches(info.Mode(), mode) {
			return false, nil
		}
		if err := os.Chmod(path, mode); err != nil {
			return false, err
		}
		return true, nil
	} else if err != nil && !errors.Is(err, os.ErrNotExist) {
		return false, err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return false, err
	}
	temp, err := os.CreateTemp(filepath.Dir(path), "."+filepath.Base(path)+".tmp-*")
	if err != nil {
		return false, err
	}
	tempPath := temp.Name()
	defer os.Remove(tempPath)
	if err := temp.Chmod(mode); err != nil {
		temp.Close()
		return false, err
	}
	if _, err := temp.Write(content); err != nil {
		temp.Close()
		return false, err
	}
	if err := temp.Sync(); err != nil {
		temp.Close()
		return false, err
	}
	if err := temp.Close(); err != nil {
		return false, err
	}
	if err := os.Rename(tempPath, path); err != nil {
		return false, fmt.Errorf("replace %s: %w", path, err)
	}
	if directory, err := os.Open(filepath.Dir(path)); err == nil {
		_ = directory.Sync()
		_ = directory.Close()
	}
	return true, nil
}
