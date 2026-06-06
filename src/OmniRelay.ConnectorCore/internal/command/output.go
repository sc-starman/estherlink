package command

import (
	"encoding/json"
	"fmt"
	"io"
)

const (
	ExitUsage      = 2
	ExitValidation = 10
	ExitConflict   = 11
	ExitApply      = 20
	ExitProbe      = 30
)

type Error struct {
	Code     string `json:"code"`
	Message  string `json:"message"`
	ExitCode int    `json:"-"`
}

func (e *Error) Error() string { return e.Message }

type Result struct {
	OK      bool   `json:"ok"`
	Command string `json:"command"`
	Message string `json:"message,omitempty"`
	Data    any    `json:"data,omitempty"`
}

type Progress struct {
	Type       string `json:"type"`
	Phase      string `json:"phase"`
	Percent    int    `json:"percent"`
	Message    string `json:"message"`
	Severity   string `json:"severity"`
	ReasonCode string `json:"reasonCode,omitempty"`
}

func WriteJSON(writer io.Writer, value any) error {
	encoder := json.NewEncoder(writer)
	encoder.SetEscapeHTML(false)
	return encoder.Encode(value)
}

func WriteProgress(writer io.Writer, phase string, percent int, message string, severity string, reasonCode string) error {
	return WriteJSON(writer, Progress{
		Type:       "progress",
		Phase:      phase,
		Percent:    percent,
		Message:    message,
		Severity:   severity,
		ReasonCode: reasonCode,
	})
}

func ValidationError(err error) error {
	return &Error{Code: "validation_failed", Message: fmt.Sprintf("gateway specification validation failed: %v", err), ExitCode: ExitValidation}
}
