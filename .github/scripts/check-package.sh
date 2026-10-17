#!/usr/bin/env bash
# Checks that a built package is the one that was meant to be built, before anything is published: a package can't be taken back
# from nuget.org once it's there, so a wrong id, a missing file or a lost README should stop the build here.
#
#   check-package.sh <package.nupkg> <id> <version>
set -euo pipefail

package="$1"
id="$2"
version="$3"

fail() {
	echo "Package check failed: $*" >&2
	exit 1
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

unzip -q "$package" -d "$work"

nuspec="$work/$id.nuspec"

[[ -f "$nuspec" ]] || fail "there is no $id.nuspec inside $package."

grep -q "<id>$id</id>" "$nuspec" || fail "the id isn't $id."
grep -q "<version>$version</version>" "$nuspec" || fail "the version isn't $version."
grep -q '<license type="expression">Apache-2.0</license>' "$nuspec" || fail "the license isn't the Apache-2.0 expression."
grep -q '<readme>README.md</readme>' "$nuspec" || fail "the README isn't named in the metadata."
grep -Eq '<repository type="git" url="https://github.com/[^"]+" commit="[0-9a-f]{40}"' "$nuspec" || fail "the repository entry has no commit."
grep -q '<dependency ' "$nuspec" && fail "the package has dependencies; it is meant to have none."

# exactly these files, apart from the ones every package has for its own bookkeeping
expected="README.md
$id.nuspec
lib/net10.0/SaneTui.Sane.dll
lib/net10.0/SaneTui.Sane.xml"

actual="$(unzip -Z1 "$package" | grep -v -E '^(\[Content_Types\]\.xml|_rels/\.rels|package/services/metadata/core-properties/[^/]+\.psmdcp)$' | sort)"

if [[ "$actual" != "$(sort <<< "$expected")" ]]; then
	diff <(sort <<< "$expected") <(echo "$actual") >&2 || true
	fail "the files in the package aren't the expected ones (< expected, > found)."
fi

# the README that was packed is the one in the repository
cmp -s "$work/README.md" src/SaneTui.Sane/README.md || fail "the README in the package differs from src/SaneTui.Sane/README.md."

echo "Package $id $version looks right: $(unzip -Z1 "$package" | wc -l) entries."
