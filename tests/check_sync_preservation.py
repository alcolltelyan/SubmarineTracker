"""Audit the integration against both pinned parents; no files are modified."""
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BASE = "aa3b40ce3e7eb9c2db9b5ad4ce2cb489755d7a5a"
FORK = "c84d745848a5ba65b665526fcd70dbe101fdd4c1"
UPSTREAM = "04c54d770f815cd416bbade1ac5b178b551a32b5"


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def blob(ref, path):
    return git("show", f"{ref}:{path}")


fork_changed = set(git("diff", "--name-only", BASE, FORK).decode().splitlines())
upstream_changed = set(git("diff", "--name-only", BASE, UPSTREAM).decode().splitlines())
for path in sorted(fork_changed - upstream_changed):
    # UPDATES.md gets an integration record, keeping all previous documentation.
    old = blob(FORK, path)
    new = (ROOT / path).read_bytes()
    if path == "UPDATES.md":
        assert new.replace(b"\r\n", b"\n").startswith(old.replace(b"\r\n", b"\n")), path
    else:
        assert new.replace(b"\r\n", b"\n") == old.replace(b"\r\n", b"\n"), path
    print("PASS unchanged fork-only file:", path)

for path in sorted(upstream_changed - fork_changed):
    assert (ROOT / path).read_bytes().replace(b"\r\n", b"\n") == blob(UPSTREAM, path).replace(b"\r\n", b"\n"), path
    print("PASS exact upstream-only file:", path)

for path in sorted(fork_changed & upstream_changed):
    if not path.endswith(".resx"):
        continue

    def resources(data):
        root = ET.fromstring(data)
        pairs = [(node.attrib["name"], node.findtext("value")) for node in root.findall("data")]
        assert len(dict(pairs)) == len(pairs), f"duplicate keys in {path}"
        return dict(pairs)

    base, fork, upstream, actual = [resources(data) for data in (
        blob(BASE, path), blob(FORK, path), blob(UPSTREAM, path), (ROOT / path).read_bytes()
    )]
    for key in base.keys() | fork.keys() | upstream.keys():
        old, ours, theirs = base.get(key), fork.get(key), upstream.get(key)
        assert ours == old or theirs == old or ours == theirs, f"semantic resource conflict: {path}/{key}"
        expected = ours if ours != old else theirs
        assert actual.get(key) == expected, f"lost resource: {path}/{key}"
    print("PASS three-way resource values:", path)

designer = "SubmarineTracker/Resources/Language.Designer.cs"
getters = lambda data: set(re.findall(rb'ResourceManager.GetString\("([^\"]+)"', data))
assert getters((ROOT / designer).read_bytes()) == getters(blob(FORK, designer)) | getters(blob(UPSTREAM, designer))
print("PASS generated resource accessor union")

# The remaining overlaps are additive settings and storage changes alongside
# the fork's one-line interval override. Verify the combined declarations.
config = (ROOT / "SubmarineTracker/Configuration.cs").read_text(encoding="utf-8-sig")
for declaration in (
    "public int CustomLootHours = 12;", "public bool WebhookLootProcessed = false;",
    'public string WebhookLootProcessedProfile = "Default";',
    "public bool ShowResourcesInAll = true;", "public bool SwapResourcesInAll = true;",
    "public Dictionary<string, List<ulong>> AccountFCs = [];",
):
    assert declaration in config, declaration
upstream_config = blob(UPSTREAM, "SubmarineTracker/Configuration.cs").decode("utf-8-sig").replace("\r\n", "\n")
without_fork_settings = config
for declaration in (
    "    public int CustomLootHours = 12;\n",
    "    public bool WebhookLootProcessed = false;\n",
    '    public string WebhookLootProcessedProfile = "Default";\n',
):
    without_fork_settings = without_fork_settings.replace(declaration, "")
assert without_fork_settings == upstream_config, "unaccounted configuration differences"
cache = (ROOT / "SubmarineTracker/DatabaseCache.cs").read_text(encoding="utf-8-sig")
assert "private const long LongDelay = 5_000;" in cache
assert "private Dictionary<ulong, StorageData> Storage = [];" in cache
upstream_cache = blob(UPSTREAM, "SubmarineTracker/DatabaseCache.cs").decode("utf-8-sig").replace("\r\n", "\n")
assert cache.replace("private const long LongDelay = 5_000; // 5s;", "private const long LongDelay = 30_000; // 30s;") == upstream_cache, "unaccounted database-cache differences"
print("PASS combined configuration and database-cache changes")
print("Preservation inventory passed")
