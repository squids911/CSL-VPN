package provisioner

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

type DHCPReservation struct {
	DeviceID string `json:"device_id"`
	VPNIPv4  string `json:"vpn_ipv4"`
	VPNMAC   string `json:"vpn_mac"`
}

var (
	deviceIDPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)
	macPattern      = regexp.MustCompile(`^02:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}$`)
)

func DecodeDHCPReservation(payload json.RawMessage) (DHCPReservation, error) {
	var reservation DHCPReservation
	decoder := json.NewDecoder(bytes.NewReader(payload))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&reservation); err != nil {
		return DHCPReservation{}, fmt.Errorf("decode DHCP reservation: %w", err)
	}
	if !deviceIDPattern.MatchString(reservation.DeviceID) {
		return DHCPReservation{}, errors.New("invalid device_id")
	}
	ip := net.ParseIP(reservation.VPNIPv4).To4()
	if ip == nil || ip[0] != 10 || ip[1] != 255 ||
		(ip[2] == 0 && ip[3] < 2) || (ip[2] == 3 && ip[3] == 255) ||
		ip[2] > 3 {
		return DHCPReservation{}, errors.New("vpn_ipv4 is outside the assigned VPN host pool")
	}
	if !macPattern.MatchString(reservation.VPNMAC) {
		return DHCPReservation{}, errors.New("vpn_mac must be a locally administered unicast MAC")
	}
	reservation.VPNIPv4 = ip.String()
	reservation.VPNMAC = strings.ToLower(reservation.VPNMAC)
	return reservation, nil
}

// ApplyDHCPReservation atomically updates only the dedicated CSL VPN include file.
// The caller must validate the dnsmasq configuration and restart dnsmasq afterwards.
func ApplyDHCPReservation(path string, reservation DHCPReservation) error {
	if _, err := DecodeDHCPReservation(mustJSON(reservation)); err != nil {
		return err
	}
	dir := filepath.Dir(path)
	if !filepath.IsAbs(path) || filepath.Clean(path) != path {
		return errors.New("reservation path must be a clean absolute path")
	}
	info, err := os.Stat(dir)
	if err != nil {
		return fmt.Errorf("inspect reservation directory: %w", err)
	}
	if !info.IsDir() {
		return errors.New("reservation parent is not a directory")
	}

	entries := map[string]string{}
	old, err := os.ReadFile(path)
	if err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("read existing reservations: %w", err)
	}
	for lineNo, raw := range strings.Split(string(old), "\n") {
		line := strings.TrimSpace(raw)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		if !strings.HasPrefix(line, "dhcp-host=") {
			return fmt.Errorf("unexpected directive in dedicated reservation file at line %d", lineNo+1)
		}
		fields := strings.Split(strings.TrimPrefix(line, "dhcp-host="), ",")
		if len(fields) != 3 || fields[2] != "infinite" || !macPattern.MatchString(fields[0]) {
			return fmt.Errorf("invalid existing reservation at line %d", lineNo+1)
		}
		ip := net.ParseIP(fields[1]).To4()
		if ip == nil || ip[0] != 10 || ip[1] != 255 || ip[2] > 3 ||
			(ip[2] == 0 && ip[3] < 2) || (ip[2] == 3 && ip[3] == 255) {
			return fmt.Errorf("invalid existing reservation address at line %d", lineNo+1)
		}
		entries[strings.ToLower(fields[0])] = ip.String()
	}
	for mac, ip := range entries {
		if mac == reservation.VPNMAC {
			delete(entries, mac)
			continue
		}
		if ip == reservation.VPNIPv4 {
			return errors.New("VPN address is already reserved for another MAC")
		}
	}
	entries[reservation.VPNMAC] = reservation.VPNIPv4

	macs := make([]string, 0, len(entries))
	for mac := range entries {
		macs = append(macs, mac)
	}
	sort.Strings(macs)
	var content strings.Builder
	content.WriteString("# Managed by CSL VPN provisioning agent; do not edit.\n")
	for _, mac := range macs {
		fmt.Fprintf(&content, "dhcp-host=%s,%s,infinite\n", mac, entries[mac])
	}

	tmp, err := os.CreateTemp(dir, ".cslvpn-reservations-*")
	if err != nil {
		return fmt.Errorf("create temporary reservation file: %w", err)
	}
	tmpName := tmp.Name()
	defer os.Remove(tmpName)
	if err := tmp.Chmod(0640); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("set reservation file permissions: %w", err)
	}
	if _, err := tmp.WriteString(content.String()); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("write reservation file: %w", err)
	}
	if err := tmp.Sync(); err != nil {
		_ = tmp.Close()
		return fmt.Errorf("sync reservation file: %w", err)
	}
	if err := tmp.Close(); err != nil {
		return fmt.Errorf("close reservation file: %w", err)
	}
	if err := os.Rename(tmpName, path); err != nil {
		return fmt.Errorf("replace reservation file: %w", err)
	}
	return nil
}

func mustJSON(value any) json.RawMessage {
	data, _ := json.Marshal(value)
	return data
}

func ValidateDNSMasqInclude(configPath, reservationPath string) error {
	data, err := os.ReadFile(configPath)
	if err != nil {
		return fmt.Errorf("read dnsmasq config: %w", err)
	}
	wantDir := filepath.Clean(filepath.Dir(reservationPath))
	for _, raw := range strings.Split(string(data), "\n") {
		line := strings.TrimSpace(raw)
		if strings.HasPrefix(line, "#") || !strings.HasPrefix(line, "conf-dir=") {
			continue
		}
		value := strings.TrimPrefix(line, "conf-dir=")
		dir := strings.SplitN(value, ",", 2)[0]
		if filepath.Clean(dir) == wantDir {
			return nil
		}
	}
	return fmt.Errorf("dnsmasq config must include conf-dir=%s", wantDir)
}

func ValidateAndRestartDNSMasq(ctx context.Context, configPath string) error {
	test := exec.CommandContext(ctx, "dnsmasq", "--test", "--conf-file="+configPath)
	if output, err := test.CombinedOutput(); err != nil {
		return fmt.Errorf("dnsmasq configuration test failed: %w: %s", err, strings.TrimSpace(string(output)))
	}
	restart := exec.CommandContext(ctx, "systemctl", "restart", "dnsmasq")
	if output, err := restart.CombinedOutput(); err != nil {
		return fmt.Errorf("dnsmasq restart failed: %w: %s", err, strings.TrimSpace(string(output)))
	}
	return nil
}
