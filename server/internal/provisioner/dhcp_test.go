package provisioner

import (
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestDecodeDHCPReservation(t *testing.T) {
	payload := json.RawMessage(`{"device_id":"12345678-1234-1234-1234-123456789abc","vpn_ipv4":"10.255.1.64","vpn_mac":"02:aa:bb:cc:dd:ee"}`)
	got, err := DecodeDHCPReservation(payload)
	if err != nil {
		t.Fatal(err)
	}
	if got.VPNIPv4 != "10.255.1.64" || got.VPNMAC != "02:aa:bb:cc:dd:ee" {
		t.Fatalf("unexpected normalized reservation: %+v", got)
	}
}

func TestDecodeDHCPReservationRejectsOutOfPoolAndUnknownFields(t *testing.T) {
	cases := []string{
		`{"device_id":"12345678-1234-1234-1234-123456789abc","vpn_ipv4":"10.255.0.1","vpn_mac":"02:aa:bb:cc:dd:ee"}`,
		`{"device_id":"12345678-1234-1234-1234-123456789abc","vpn_ipv4":"10.255.1.64","vpn_mac":"02:aa:bb:cc:dd:ee","command":"id"}`,
		`{"device_id":"12345678-1234-1234-1234-123456789abc","vpn_ipv4":"192.168.1.10","vpn_mac":"02:aa:bb:cc:dd:ee"}`,
	}
	for _, payload := range cases {
		if _, err := DecodeDHCPReservation(json.RawMessage(payload)); err == nil {
			t.Fatalf("expected payload rejection: %s", payload)
		}
	}
}

func TestApplyDHCPReservationPreservesAndSortsEntries(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cslvpn-reservations.conf")
	if err := os.WriteFile(path, []byte("dhcp-host=02:00:00:00:00:01,10.255.0.2,infinite\n"), 0600); err != nil {
		t.Fatal(err)
	}
	reservation := DHCPReservation{
		DeviceID: "12345678-1234-1234-1234-123456789abc",
		VPNIPv4: "10.255.1.64",
		VPNMAC: "02:aa:bb:cc:dd:ee",
	}
	if err := ApplyDHCPReservation(path, reservation); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	content := string(data)
	first := strings.Index(content, "02:00:00:00:00:01")
	second := strings.Index(content, "02:aa:bb:cc:dd:ee")
	if first < 0 || second < 0 || first >= second {
		t.Fatalf("reservations were not preserved and sorted: %s", content)
	}
}

func TestApplyDHCPReservationRejectsAddressCollision(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "cslvpn-reservations.conf")
	if err := os.WriteFile(path, []byte("dhcp-host=02:00:00:00:00:01,10.255.1.64,infinite\n"), 0600); err != nil {
		t.Fatal(err)
	}
	err := ApplyDHCPReservation(path, DHCPReservation{
		DeviceID: "12345678-1234-1234-1234-123456789abc",
		VPNIPv4: "10.255.1.64",
		VPNMAC: "02:aa:bb:cc:dd:ee",
	})
	if err == nil {
		t.Fatal("expected address collision to be rejected")
	}
}
