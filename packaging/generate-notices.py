#!/usr/bin/env python3
"""Generates THIRD-PARTY-NOTICES.txt and third-party/*.txt from the NuGet packages the desktop app restores.

Run after `dotnet restore`:  python3 packaging/generate-notices.py
Packages are read from Kubism.Desktop/obj/project.assets.json; license data from each package's nuspec and license files.
"""
import json, os, re, shutil, sys
import xml.etree.ElementTree as ET

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = json.load(open(os.path.join(root, "Kubism.Desktop", "obj", "project.assets.json")))
pkgdir = next(iter(assets["packageFolders"])).rstrip("/")
out_dir = os.path.join(root, "third-party")
shutil.rmtree(out_dir, ignore_errors=True)
os.makedirs(out_dir)

SKIP = re.compile(r"^(Microsoft\.NETCore\.App\.|Microsoft\.AspNetCore\.App\.|runtime\.|Microsoft\.DotNet\.ILCompiler|Microsoft\.NET\.ILLink|NETStandard\.Library)", re.I)
rows = []
for lib, meta in sorted(assets["libraries"].items()):
    if meta.get("type") != "package": continue
    name, ver = lib.split("/")
    if SKIP.match(name): continue
    base = os.path.join(pkgdir, name.lower(), ver)
    nuspec = os.path.join(base, name.lower() + ".nuspec")
    lic = copy = url = authors = ""
    licfile = None
    if os.path.exists(nuspec):
        t = ET.parse(nuspec).getroot()
        ns = {"n": t.tag.split("}")[0].strip("{")}
        md = t.find("n:metadata", ns)
        def g(tag):
            e = md.find("n:" + tag, ns)
            return (e.text or "").strip() if e is not None else ""
        e = md.find("n:license", ns)
        if e is not None:
            if e.get("type") == "expression": lic = (e.text or "").strip()
            elif e.get("type") == "file": licfile = (e.text or "").strip()
        url = g("projectUrl"); copy = g("copyright"); authors = g("authors")
        if not lic and not licfile: lic = g("licenseUrl") or "see package"
    found = None
    for cand in ([licfile] if licfile else []) + ["LICENSE", "LICENSE.txt", "LICENSE.md", "license.txt", "License.txt", "LICENSE.TXT", "NOTICE", "NOTICE.txt", "THIRD-PARTY-NOTICES.txt"]:
        if cand and os.path.exists(os.path.join(base, cand)): found = os.path.join(base, cand); break
    stored = ""
    if found and os.path.getsize(found) < 400_000:
        stored = f"{name}-{os.path.basename(found)}".replace("/", "_")
        if not stored.lower().endswith((".txt", ".md")): stored += ".txt"
        shutil.copy(found, os.path.join(out_dir, stored))
    rows.append((name, ver, lic or ("see " + (licfile or "package")), copy, url, authors, stored))

lines = ["Kubism - third-party notices", "============================", "",
         "Kubism itself is released under the MIT License (see LICENSE).",
         "It includes or links the software listed below. Each component remains under its own license;",
         "the license texts shipped by the packages are in the \"third-party\" folder next to this file.", "",
         "NuGet packages", "--------------", ""]
for name, ver, lic, copy, url, authors, stored in rows:
    lines.append(f"{name} {ver}")
    lines.append(f"  License: {lic}")
    if copy: lines.append(f"  {copy}")
    elif authors: lines.append(f"  Authors: {authors}")
    if url: lines.append(f"  {url}")
    if stored: lines.append(f"  Text: third-party/{stored}")
    lines.append("")
lines += ["Fonts", "-----", "", "Inter (Avalonia.Fonts.Inter): SIL Open Font License 1.1. https://rsms.me/inter/", "",
          "Icons", "-----", "", "The Kubism icon and the vector icons in the app are original work released under the MIT License.", ""]
open(os.path.join(root, "THIRD-PARTY-NOTICES.txt"), "w").write("\n".join(lines))
print(f"{len(rows)} packages, {len(os.listdir(out_dir))} license files")
