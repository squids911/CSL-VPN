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

func TestDeterministicVPNMACIsStableLocallyAdministeredUnicast(t *testing.T) {
	fingerprint := bytes.Repeat([]byte{0x5a}, 32)
	first := deterministicVPNMAC(fingerprint)
	second := deterministicVPNMAC(fingerprint)
	if first != second {
		t.Fatalf("MAC changed for the same fingerprint: %q != %q", first, second)
	}
	if len(first) != 17 || first[:3] != "02:" {
		t.Fatalf("expected locally administered unicast MAC, got %q", first)
	}
	if deterministicVPNMAC(bytes.Repeat([]byte{0x5b}, 32)) == first {
		t.Fatal("different fingerprints unexpectedly produced the same MAC")
	}
}
