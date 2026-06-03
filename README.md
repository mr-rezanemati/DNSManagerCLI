DNS Manager CLI

A powerful Windows CLI tool for managing DNS settings with profile support, speed benchmarking, and DNS resolution testing.
Features

    Set/Clear DNS on any network adapter
    DNS speed benchmark against popular servers
    DNS resolution testing
    Profile management with Registry storage
    Import/Export profiles (JSON)
    Admin auto-elevation

Requirements

    Windows 10/11
    .NET 10 Runtime
    Administrator privileges (for DNS changes)

Usage

dns.exe --set 1.1.1.1 8.8.8.8 --name "Cloudflare+Google"
dns.exe --show
dns.exe --benchmark
dns.exe --about
