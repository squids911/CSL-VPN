package httpapi

import (
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestSessionEndpointRequiresDeviceCredential(t *testing.T) {
	handler := New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)), "0123456789abcdef0123456789abcdef").Handler()
	request := httptest.NewRequest(http.MethodPost, "/api/v1/sessions", nil)
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d: %s", http.StatusUnauthorized, response.Code, response.Body.String())
	}
}

func TestHeartbeatEndpointRequiresDeviceCredential(t *testing.T) {
	handler := New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)), "0123456789abcdef0123456789abcdef").Handler()
	request := httptest.NewRequest(http.MethodPost, "/api/v1/heartbeat", nil)
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d: %s", http.StatusUnauthorized, response.Code, response.Body.String())
	}
}
