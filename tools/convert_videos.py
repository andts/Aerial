#!/usr/bin/env python3
"""Convert OrangeJedi/Aerial videos.json into Aerial's IdAsset[] envelope format."""
import json, sys, collections

def convert(src_path, dst_path):
    with open(src_path, encoding="utf-8") as f:
        src = json.load(f)
    if not isinstance(src, list):
        sys.exit("expected a top-level JSON array")

    assets = []
    seen = set()
    for v in src:
        vid = v.get("id")
        srcs = v.get("src") or {}
        if not vid or not srcs.get("H2641080p"):
            print("SKIP (no id or no H.264 source): %r" % v.get("name"), file=sys.stderr)
            continue
        if vid in seen:
            print("SKIP (duplicate id): %s" % vid, file=sys.stderr)
            continue
        seen.add(vid)
        assets.append({
            "id": vid,
            "accessibilityLabel": v.get("accessibilityLabel") or v.get("name") or "Unknown",
            "name": v.get("name") or v.get("accessibilityLabel") or "Unknown",
            "category": v.get("type") or "",
            "timeOfDay": v.get("timeOfDay") or "",
            "url": srcs.get("H2641080p"),
            "src": {
                "H2641080p": srcs.get("H2641080p") or "",
                "H2651080p": srcs.get("H2651080p") or "",
                "H2654k":    srcs.get("H2654k") or "",
            },
            "pointsOfInterest": v.get("pointsOfInterest") or {},
        })

    assets.sort(key=lambda a: (a["accessibilityLabel"].upper(), a["timeOfDay"], a["id"]))
    out = [{"id": "bundled", "assets": assets}]
    with open(dst_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print("assets: %d" % len(assets))
    print("timeOfDay: %r" % dict(collections.Counter(a["timeOfDay"] for a in assets)))
    print("category:  %r" % dict(collections.Counter(a["category"] for a in assets)))
    missing = [k for a in assets for k, u in a["src"].items() if not u]
    print("empty src entries: %d" % len(missing))

if __name__ == "__main__":
    convert(sys.argv[1], sys.argv[2])
