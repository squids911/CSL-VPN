package httpapi

import (
	"bytes"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"testing"
)

func testServer() http.Handler {
	return New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)), "0123456789abcdef0123456789abcdef").Handler()
}

func TestEnrollRejectsMalformedFingerprint(t *testing.T) {
	body := []byte(`{"bootstrap_token":"not-a-token","device_name":"test-pc","device_fingerprint":"bad","device_credential":"0000000000000000000000000000000000000000000000000000000000000000"}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}

func TestEnrollRejectsUnknownJSONFields(t *testing.T) {
	body := []byte(`{"bootstrap_token":"not-a-token","device_name":"test-pc","device_fingerprint":"bad","device_credential":"bad","is_admin":true}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}

func TestAdminEnrollmentTokenEndpointRequiresAdminToken(t *testing.T) {
	body := []byte(`{"device_name":"test-pc","expires_in_minutes":60}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/admin/enrollment-tokens", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d: %s", http.StatusUnauthorized, response.Code, response.Body.String())
	}
}
