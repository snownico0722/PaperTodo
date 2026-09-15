from pathlib import Path
import re


def replace_once(path: str, old: str, new: str) -> None:
    p = Path(path)
    text = p.read_text(encoding="utf-8-sig")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected exactly one match, found {count}: {old[:120]!r}")
    p.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")


release_path = Path(".github/workflows/release.yml")
release_text = release_path.read_text(encoding="utf-8-sig")

if '"release_version=$releaseVersion" >> $env:GITHUB_OUTPUT' not in release_text:
    replace_once(
        str(release_path),
        '''          # Multiple PropertyGroups exist; .PropertyGroup.Version is an array that stringifies
          # with a trailing space (e.g. "3.0 "). Read the single Version element instead.
          $versionNodes = $project.SelectNodes('/Project/PropertyGroup/Version')
          if ($versionNodes.Count -ne 1) {
            throw "PaperTodo.csproj must define exactly one <Version> element (found $($versionNodes.Count))."
          }
          $version = ([string]$versionNodes[0].InnerText).Trim()
          if ([string]::IsNullOrWhiteSpace($version) -or $version -match '\\s') {
            throw "PaperTodo.csproj <Version> is missing or contains whitespace: '$version'."
          }

          "version=$version" >> $env:GITHUB_OUTPUT
          "self_contained_exe=PaperTodo-v$version-win-x64-self-contained.exe" >> $env:GITHUB_OUTPUT
          "framework_dependent_exe=PaperTodo-v$version-win-x64-no-runtime.exe" >> $env:GITHUB_OUTPUT''',
        '''          # Keep the SDK build version SemVer-compatible while allowing the public release
          # label to follow PaperTodo's compact naming (for example 4.0beta1).
          $versionNodes = $project.SelectNodes('/Project/PropertyGroup/Version')
          $informationalVersionNodes = $project.SelectNodes('/Project/PropertyGroup/InformationalVersion')
          if ($versionNodes.Count -ne 1) {
            throw "PaperTodo.csproj must define exactly one <Version> element (found $($versionNodes.Count))."
          }
          if ($informationalVersionNodes.Count -ne 1) {
            throw "PaperTodo.csproj must define exactly one <InformationalVersion> element (found $($informationalVersionNodes.Count))."
          }
          $buildVersion = ([string]$versionNodes[0].InnerText).Trim()
          $releaseVersion = ([string]$informationalVersionNodes[0].InnerText).Trim()
          if ([string]::IsNullOrWhiteSpace($buildVersion) -or $buildVersion -match '\\s') {
            throw "PaperTodo.csproj <Version> is missing or contains whitespace: '$buildVersion'."
          }
          if ([string]::IsNullOrWhiteSpace($releaseVersion) -or $releaseVersion -match '\\s') {
            throw "PaperTodo.csproj <InformationalVersion> is missing or contains whitespace: '$releaseVersion'."
          }
          if ($releaseVersion -notmatch '^[A-Za-z0-9_.-]+$') {
            throw "PaperTodo.csproj <InformationalVersion> contains unsupported release filename/tag characters: '$releaseVersion'."
          }

          "build_version=$buildVersion" >> $env:GITHUB_OUTPUT
          "release_version=$releaseVersion" >> $env:GITHUB_OUTPUT
          "self_contained_exe=PaperTodo-v$releaseVersion-win-x64-self-contained.exe" >> $env:GITHUB_OUTPUT
          "framework_dependent_exe=PaperTodo-v$releaseVersion-win-x64-no-runtime.exe" >> $env:GITHUB_OUTPUT''',
    )
    replace_once(
        str(release_path),
        '          name: PaperTodo-v${{ steps.meta.outputs.version }}-win-x64',
        '          name: PaperTodo-v${{ steps.meta.outputs.release_version }}-win-x64',
    )
    replace_once(
        str(release_path),
        '              $tag = "v${{ steps.meta.outputs.version }}"',
        '              $tag = "v${{ steps.meta.outputs.release_version }}"',
    )
    replace_once(
        str(release_path),
        '          $expectedTag = "v${{ steps.meta.outputs.version }}"',
        '          $expectedTag = "v${{ steps.meta.outputs.release_version }}"',
    )
    print("Updated release workflow to use InformationalVersion for public release naming.")
else:
    print("Release workflow public-version mapping is already applied.")

csproj = Path("PaperTodo.csproj").read_text(encoding="utf-8-sig")
required_project_values = [
    "<Version>4.0.0-beta1</Version>",
    "<InformationalVersion>4.0beta1</InformationalVersion>",
]
for value in required_project_values:
    if value not in csproj:
        raise SystemExit(f"Missing expected project metadata: {value}")

for path in ("CHANGELOG.md", "doc/CHANGELOG.en.md"):
    text = Path(path).read_text(encoding="utf-8-sig")
    if len(re.findall(r"^### v4\.0beta1$", text, flags=re.MULTILINE)) != 1:
        raise SystemExit(f"{path}: expected exactly one v4.0beta1 section")

print("4.0beta1 release workflow and changelog metadata validated.")
