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

func TestProvisioningAgentEndpointsRequireDedicatedToken(t *testing.T) {
	request := httptest.NewRequest(http.MethodPost, "/api/v1/agent/provisioning/claim", bytes.NewReader([]byte(`{"worker_id":"vpn-host-01"}`)))
	response := httptest.NewRecorder()

	testServer().ServeHTTP(response, request)

	if response.Code != http.StatusUnauthorized {
		t.Fatalf("expected status %d, got %d: %s", http.StatusUnauthorized, response.Code, response.Body.String())
	}
}

func TestProvisioningClaimValidatesWorkerIDBeforeDatabaseAccess(t *testing.T) {
	const token = "fedcba9876543210fedcba9876543210"
	handler := New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)),
		"0123456789abcdef0123456789abcdef", token).Handler()
	request := httptest.NewRequest(http.MethodPost, "/api/v1/agent/provisioning/claim",
		bytes.NewReader([]byte(`{"worker_id":"bad\nworker"}`)))
	request.Header.Set("Authorization", "Bearer "+token)
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}

func TestProvisioningClaimRejectsUnknownJobTypeBeforeDatabaseAccess(t *testing.T) {
	const token = "fedcba9876543210fedcba9876543210"
	handler := New(nil, slog.New(slog.NewTextHandler(io.Discard, nil)),
		"0123456789abcdef0123456789abcdef", token).Handler()
	request := httptest.NewRequest(http.MethodPost, "/api/v1/agent/provisioning/claim",
		bytes.NewReader([]byte(`{"worker_id":"vpn-host-01","job_type":"arbitrary_command"}`)))
	request.Header.Set("Authorization", "Bearer "+token)
	response := httptest.NewRecorder()

	handler.ServeHTTP(response, request)

	if response.Code != http.StatusBadRequest {
		t.Fatalf("expected status %d, got %d: %s", http.StatusBadRequest, response.Code, response.Body.String())
	}
}
