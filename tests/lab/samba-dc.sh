#!/usr/bin/env bash
# Starts a throwaway Samba Active Directory domain controller with synthetic users and groups, for the
# directory sync integration tests. Test use only: it turns off LDAP signing so the tests can use a simple bind.
# Usage (as root): tests/lab/samba-dc.sh [dir]   then export DATASCOPE_TEST_LDAP as printed.
set -euo pipefail
# Package installs can start the stock Samba services, which would hold the ports.
if command -v systemctl >/dev/null; then systemctl stop smbd nmbd winbind samba-ad-dc 2>/dev/null || true; fi
dir="${1:-/tmp/datascope-samba}"
pass='Lab-only-Passw0rd!'
realm=NORTHWIND.TEST
base='DC=northwind,DC=test'
# samba-tool needs the Python version Samba was built for (3.12 on Ubuntu 24.04), which may not be the default python3.
py="$(command -v python3.12 || command -v python3)"
samba_tool() { "$py" "$(command -v samba-tool)" "$@"; }

if pgrep -x samba >/dev/null; then pkill -x samba || true; sleep 2; fi
rm -rf "$dir"
mkdir -p "$dir"
samba_tool domain provision --targetdir="$dir" --realm="$realm" --domain=NORTHWIND --server-role=dc \
  --dns-backend=NONE --adminpass="$pass" --use-rfc2307 \
  --option="interfaces = lo" --option="bind interfaces only = yes" >/dev/null 2>&1
conf="$dir/etc/smb.conf"
sed -i 's/^\[global\]$/[global]\n\tldap server require strong auth = no/' "$conf"
samba -s "$conf" -D
for i in $(seq 1 60); do
  if samba_tool user list -H ldap://127.0.0.1 -U Administrator --password="$pass" >/dev/null 2>&1; then break; fi
  sleep 1
done

st() { samba_tool "$@" -H "ldap://127.0.0.1" -U Administrator --password="$pass" >/dev/null; }
user() { st user create "$1" "$pass" --given-name="$2" --surname=Lab --department="$3"; }
user alice Alice HR
user bob Bob Finance
user carol Carol HR
user dave Dave Finance
user eve Eve Operations
user frank Frank Operations
st user disable carol
for g in HR_Read HR_Write Finance_Read Finance_Reviewers AllStaff Loop_A Loop_B Big_Group; do st group add "$g"; done
st group add Newsletter --group-type=Distribution
st group addmembers HR_Read alice,carol
st group addmembers HR_Write alice
st group addmembers Finance_Read bob,Finance_Reviewers
st group addmembers Finance_Reviewers dave
st group addmembers AllStaff "Domain Users"
st group addmembers Loop_A Loop_B,frank
st group addmembers Loop_B Loop_A
st group addmembers Newsletter eve

# Reporting lines: alice and carol report to bob (the AD "manager" attribute holds the manager's DN).
dn_of() { ldbsearch -H "$dir/private/sam.ldb" "(sAMAccountName=$1)" dn | sed -n 's/^dn: //p'; }
for u in alice carol; do
  printf 'dn: %s\nchangetype: modify\nreplace: manager\nmanager: %s\n' "$(dn_of "$u")" "$(dn_of bob)" > "$dir/manager.ldif"
  ldbmodify -H "$dir/private/sam.ldb" "$dir/manager.ldif" >/dev/null
done

# More members than one LDAP response returns (1,500), so the reader must use ranged retrieval.
ldif="$dir/big.ldif"
: > "$ldif"
for i in $(seq 1 1600); do
  printf 'dn: CN=bulk%04d,CN=Users,%s\nobjectClass: user\nsAMAccountName: bulk%04d\nuserAccountControl: 514\n\n' "$i" "$base" "$i" >> "$ldif"
done
ldbadd -H "$dir/private/sam.ldb" "$ldif" >/dev/null
members="$dir/members.ldif"
printf 'dn: CN=Big_Group,CN=Users,%s\nchangetype: modify\nadd: member\n' "$base" > "$members"
for i in $(seq 1 1600); do printf 'member: CN=bulk%04d,CN=Users,%s\n' "$i" "$base" >> "$members"; done
ldbmodify -H "$dir/private/sam.ldb" "$members" >/dev/null

# A member from another (fictional) domain, which Data Scope cannot expand. Samba creates the
# foreign security principal object when a member is added by SID.
st group add Partners
st group addmembers Partners bob
printf 'dn: CN=Partners,CN=Users,%s\nchangetype: modify\nadd: member\nmember: <SID=S-1-5-21-9000-9000-9000-4001>\n' "$base" > "$dir/partners.ldif"
ldbmodify -H "$dir/private/sam.ldb" "$dir/partners.ldif" >/dev/null

echo "export DATASCOPE_TEST_LDAP='Server=127.0.0.1;Port=389;User=Administrator@northwind.test;Password=$pass'"
