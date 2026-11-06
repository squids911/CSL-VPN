package httpapi

import (
	"bytes"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestEnrollRejectsMalformedDeviceCredential(t *testing.T) {
	handler := New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)), "0123456789abcdef0123456789abcdef").Handler()
	body := []byte(`{"bootstrap_token":"0123456789abcdef0123456789abcdef","device_name":"test-pc","device_fingerprint":"0000000000000000000000000000000000000000000000000000000000000000","device_credential":"bad"}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}
