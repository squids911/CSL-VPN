package provisioner

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

func TestSoftEtherUsernameDeterministic(t *testing.T) {
	id := "11111111-2222-3333-4444-555555555555"
	a, b := SoftEtherUsername(id), SoftEtherUsername(id)
	if a != b || !softEtherUsernamePattern.MatchString(a) {
		t.Fatalf("unexpected username %q", a)
	}
	if SoftEtherUsername("99999999-2222-3333-4444-555555555555") == a {
		t.Fatal("usernames must differ per device")
	}
}

func TestDecodeSoftEtherAccount(t *testing.T) {
	good := json.RawMessage(`{"device_id":"11111111-2222-3333-4444-555555555555","device_name":"win-01"}`)
	if _, err := DecodeSoftEtherAccount(good); err != nil {
		t.Fatalf("valid payload rejected: %v", err)
	}
	bad := []json.RawMessage{
		json.RawMessage(`{"device_id":"nope","device_name":"x"}`),
		json.RawMessage(`{"device_id":"11111111-2222-3333-4444-555555555555","device_name":""}`),
		json.RawMessage(`{"device_id":"11111111-2222-3333-4444-555555555555","device_name":"x","extra":1}`),
	}
	for _, payload := range bad {
		if _, err := DecodeSoftEtherAccount(payload); err == nil {
			t.Fatalf("invalid payload accepted: %s", payload)
		}
	}
}

func TestValidateSoftEtherCredentials(t *testing.T) {
	if err := ValidateSoftEtherCredentials("dev-0123456789abcdef0123", "0123456789abcdef0123456789abcdef0123456789abcdef"); err != nil {
		t.Fatalf("valid credentials rejected: %v", err)
	}
	if err := ValidateSoftEtherCredentials("admin", "0123456789abcdef0123456789abcdef0123456789abcdef"); err == nil {
		t.Fatal("bad username accepted")
	}
}

func TestLoadVPNCmdPasswordFilePermissions(t *testing.T) {
	dir := t.TempDir()
	bin := filepath.Join(dir, "vpncmd")
	if err := os.WriteFile(bin, []byte("#!/bin/sh\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	pw := filepath.Join(dir, "hub.password")
	if err := os.WriteFile(pw, []byte("secret\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadVPNCmd(bin, "127.0.0.1:443", "CSL", pw); err == nil {
		t.Fatal("world-readable password file accepted")
	}
	if err := os.Chmod(pw, 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := LoadVPNCmd(bin, "127.0.0.1:443", "CSL", pw); err != nil {
		t.Fatalf("0600 password file rejected: %v", err)
	}
}
