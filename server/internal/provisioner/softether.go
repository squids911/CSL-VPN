package provisioner

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
)

type SoftEtherAccount struct {
	DeviceID   string `json:"device_id"`
	DeviceName string `json:"device_name"`
}

var (
	softEtherUsernamePattern = regexp.MustCompile(`^dev-[0-9a-f]{20}$`)
	softEtherPasswordPattern = regexp.MustCompile(`^[0-9a-f]{48}$`)
	hubNamePattern           = regexp.MustCompile(`^[A-Za-z0-9_-]{1,31}$`)
)

func SoftEtherUsername(deviceID string) string {
	sum := sha256.Sum256([]byte(deviceID))
	return "dev-" + hex.EncodeToString(sum[:10])
}

func DecodeSoftEtherAccount(payload json.RawMessage) (SoftEtherAccount, error) {
	var account SoftEtherAccount
	decoder := json.NewDecoder(bytes.NewReader(payload))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&account); err != nil {
		return SoftEtherAccount{}, fmt.Errorf("decode SoftEther account: %w", err)
	}
	if !deviceIDPattern.MatchString(account.DeviceID) {
		return SoftEtherAccount{}, errors.New("invalid device_id")
	}
	if account.DeviceName == "" || len(account.DeviceName) > 64 ||
		strings.ContainsAny(account.DeviceName, "\r\n\x00") {
		return SoftEtherAccount{}, errors.New("invalid device_name")
	}
	return account, nil
}

func ValidateSoftEtherCredentials(username, password string) error {
	if !softEtherUsernamePattern.MatchString(username) {
		return errors.New("invalid SoftEther username")
	}
	if !softEtherPasswordPattern.MatchString(password) {
		return errors.New("invalid SoftEther password")
	}
	return nil
}

type VPNCmd struct {
	BinaryPath    string
	Server        string
	Hub           string
	AdminPassword string
}

func LoadVPNCmd(binaryPath, server, hub, adminPasswordFile string) (VPNCmd, error) {
	if !filepath.IsAbs(binaryPath) || filepath.Clean(binaryPath) != binaryPath {
		return VPNCmd{}, errors.New("vpncmd path must be a clean absolute path")
	}
	if !hubNamePattern.MatchString(hub) {
		return VPNCmd{}, errors.New("invalid SoftEther hub name")
	}
	if server == "" || strings.ContainsAny(server, "\r\n\x00 ") {
		return VPNCmd{}, errors.New("invalid SoftEther server endpoint")
	}
	info, err := os.Stat(adminPasswordFile)
	if err != nil {
		return VPNCmd{}, fmt.Errorf("stat SoftEther admin password file: %w", err)
	}
	if info.Mode().Perm()&0o077 != 0 {
		return VPNCmd{}, errors.New("SoftEther admin password file must not be group/world accessible")
	}
	data, err := os.ReadFile(adminPasswordFile)
	if err != nil {
		return VPNCmd{}, fmt.Errorf("read SoftEther admin password: %w", err)
	}
	password := strings.TrimRight(string(data), "\r\n")
	if password == "" || strings.ContainsAny(password, "\r\n\x00") {
		return VPNCmd{}, errors.New("empty or malformed SoftEther admin password")
	}
	return VPNCmd{BinaryPath: binaryPath, Server: server, Hub: hub, AdminPassword: password}, nil
}

func redact(output string, secrets []string) string {
	for _, secret := range secrets {
		if secret != "" {
			output = strings.ReplaceAll(output, secret, "[redacted]")
		}
	}
	if len(output) > 480 {
		output = output[:480]
	}
	return strings.TrimSpace(output)
}

func (v VPNCmd) run(ctx context.Context, stdin string, secrets []string, args ...string) error {
	full := append([]string{v.Server, "/SERVER", "/HUB:" + v.Hub, "/CMD"}, args...)
	cmd := exec.CommandContext(ctx, v.BinaryPath, full...)
	cmd.Stdin = strings.NewReader(stdin)
	output, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("vpncmd %s: %w: %s", args[0], err, redact(string(output), secrets))
	}
	return nil
}

func (v VPNCmd) EnsureAccount(ctx context.Context, username, password string) error {
	if err := ValidateSoftEtherCredentials(username, password); err != nil {
		return err
	}
	admin := v.AdminPassword + "\n"
	secrets := []string{v.AdminPassword, password}
	if err := v.run(ctx, admin, secrets, "UserGet", username); err != nil {
		if err := v.run(ctx, admin, secrets,
			"UserCreate", username, "/GROUP:none", "/REALNAME:none", "/NOTE:csl-vpn"); err != nil {
			return fmt.Errorf("create SoftEther user: %w", err)
		}
	}
	if err := v.run(ctx, admin+password+"\n"+password+"\n", secrets,
		"UserPasswordSet", username); err != nil {
		return fmt.Errorf("set SoftEther password: %w", err)
	}
	if err := v.run(ctx, admin, secrets,
		"UserPolicySet", username, "/NAME:max_connection", "/VALUE:1"); err != nil {
		return fmt.Errorf("apply SoftEther policy: %w", err)
	}
	return nil
}
