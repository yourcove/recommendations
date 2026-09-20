#!/usr/bin/env python3
"""
Restore scene ratings from a Stash SQLite DB into a running Cove instance.

Matches Stash scenes to Cove videos by the primary file's **oshash** fingerprint, then sets the
overall rating via Cove's engagement API. Stash `scenes.rating` and Cove ratings are both 0-100,
so values map directly. Idempotent: re-running just re-sets the same ratings.

Defaults to a DRY RUN (reports matches, applies nothing). Pass --apply to actually write ratings.

Examples:
  # dry run against the 5073 instance
  python restore-stash-ratings.py --base http://localhost:5073
  # apply to both instances
  python restore-stash-ratings.py --base http://localhost:5073 --apply
  python restore-stash-ratings.py --base http://localhost:9999 --apply
"""
import argparse
import json
import sqlite3
import sys
import urllib.error
import urllib.request

DEFAULT_DB = r"C:\Coding\Testing\Stash-PornServer\stash-go.sqlite"


def http(base, path, method="GET", body=None, timeout=60):
    url = base.rstrip("/") + path
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            txt = r.read().decode()
            return r.status, (json.loads(txt) if txt else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()[:300]
    except Exception as e:  # noqa: BLE001
        return None, str(e)


def load_stash_ratings(db_path):
    con = sqlite3.connect(db_path)
    try:
        rows = con.execute(
            """
            SELECT s.id, s.rating, ff.fingerprint
            FROM scenes s
            JOIN scenes_files sf ON sf.scene_id = s.id AND sf."primary" = 1
            JOIN files_fingerprints ff ON ff.file_id = sf.file_id AND ff.type = 'oshash'
            WHERE s.rating IS NOT NULL
            """
        ).fetchall()
    finally:
        con.close()
    # (scene_id, rating, oshash)
    return [(r[0], int(r[1]), r[2]) for r in rows]


def find_video_by_oshash(base, oshash):
    body = {
        "findFilter": {"page": 1, "perPage": 2},
        "objectFilter": {
            "fingerprintCriterion": {"type": "oshash", "value": oshash, "modifier": "equals"}
        },
    }
    status, data = http(base, "/api/videos/find", "POST", body)
    if status != 200 or not isinstance(data, dict):
        return None, f"find failed (status={status}, body={str(data)[:120]})"
    items = data.get("items") or []
    if not items:
        return None, "no-match"
    if len(items) > 1:
        return None, f"ambiguous ({len(items)} videos share this oshash)"
    return items[0]["id"], None


def set_rating(base, video_id, rating):
    status, data = http(base, f"/api/engagement/Video/{video_id}/rating", "PUT",
                        {"value": rating, "aspect": "overall"})
    return status, data


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default="http://localhost:5073", help="Cove API base URL")
    ap.add_argument("--db", default=DEFAULT_DB, help="Path to the Stash SQLite database")
    ap.add_argument("--apply", action="store_true", help="Actually write ratings (otherwise dry run)")
    ap.add_argument("--limit", type=int, default=0, help="Only process the first N (for testing)")
    args = ap.parse_args()

    ratings = load_stash_ratings(args.db)
    if args.limit:
        ratings = ratings[: args.limit]
    print(f"Loaded {len(ratings)} rated scenes (with oshash) from Stash.")
    print(f"Target: {args.base}   Mode: {'APPLY' if args.apply else 'DRY RUN'}\n")

    matched = applied = no_match = ambiguous = errors = 0
    unmatched_samples = []

    for i, (scene_id, rating, oshash) in enumerate(ratings, 1):
        video_id, err = find_video_by_oshash(args.base, oshash)
        if video_id is None:
            if err == "no-match":
                no_match += 1
                if len(unmatched_samples) < 10:
                    unmatched_samples.append((scene_id, oshash, rating))
            elif err and err.startswith("ambiguous"):
                ambiguous += 1
            else:
                errors += 1
                print(f"  ! scene {scene_id}: {err}")
            continue

        matched += 1
        if args.apply:
            status, data = set_rating(args.base, video_id, rating)
            if status == 200:
                applied += 1
            else:
                errors += 1
                print(f"  ! scene {scene_id} -> video {video_id}: set-rating status {status} ({str(data)[:120]})")

        if i % 50 == 0:
            print(f"  ...processed {i}/{len(ratings)} (matched {matched}, applied {applied})")

    print("\n--- Summary ---")
    print(f"  rated scenes:     {len(ratings)}")
    print(f"  matched to video: {matched}")
    if args.apply:
        print(f"  ratings applied:  {applied}")
    print(f"  no Cove match:    {no_match}")
    print(f"  ambiguous oshash: {ambiguous}")
    print(f"  errors:           {errors}")
    if unmatched_samples:
        print("\n  Sample unmatched (scene_id, oshash, rating):")
        for s in unmatched_samples:
            print(f"    {s}")
    if not args.apply:
        print("\n  Dry run only — re-run with --apply to write these ratings.")


if __name__ == "__main__":
    sys.exit(main())
