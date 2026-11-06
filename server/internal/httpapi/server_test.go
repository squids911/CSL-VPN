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

func TestEnrollRejectsInvalidBootstrapToken(t *testing.T) {
	body := []byte(`{"bootstrap_token":"wrong","device_name":"test-pc","device_fingerprint":"not-a-fingerprint"}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d: %s", http.StatusUnauthorized, response.Code, response.Body.String())
	}
}

func TestEnrollRejectsMalformedFingerprint(t *testing.T) {
	body := []byte(`{"bootstrap_token":"0123456789abcdef0123456789abcdef","device_name":"test-pc","device_fingerprint":"bad"}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}

func TestEnrollRejectsUnknownJSONFields(t *testing.T) {
	body := []byte(`{"bootstrap_token":"0123456789abcdef0123456789abcdef","device_name":"test-pc","device_fingerprint":"bad","is_admin":true}`)
	request := httptest.NewRequest(http.MethodPost, "/api/v1/enroll", bytes.NewReader(body))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}
