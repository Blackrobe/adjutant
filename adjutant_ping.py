"""Send a message to the Adjutant desktop companion.

Usage:
    py -3 -I adjutant_ping.py --from NAME --msg TEXT [--state STATE] [--tab TAB]

STATE is one of idle, busy, attention, error, done and holds the Adjutant in that state for a few
seconds. TAB defaults to $HERDR_TAB_ID, so a click on the Adjutant jumps to the sender's herdr tab.
Exits 0 silently when the Adjutant is not running, unless --verbose is given.
"""

import argparse
import json
import os
import sys
import urllib.request

STATES = ("idle", "busy", "attention", "error", "done")


def main() -> int:
    parser = argparse.ArgumentParser(description="Ping the Adjutant")
    parser.add_argument("--from", dest="sender", required=True)
    parser.add_argument("--msg", required=True)
    parser.add_argument("--state", choices=STATES)
    parser.add_argument("--tab", default=os.environ.get("HERDR_TAB_ID"))
    parser.add_argument("--port", type=int, default=47777)
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    body = {"from": args.sender[:40], "msg": args.msg[:300]}
    if args.state:
        body["state"] = args.state
    if args.tab:
        body["tab"] = args.tab
    request = urllib.request.Request(
        f"http://127.0.0.1:{args.port}/ping",
        data=json.dumps(body).encode("utf-8"),
        method="POST",
        headers={"Content-Type": "application/json", "X-Adjutant": "1"},
    )
    # Never route the loopback call through a proxy from the environment.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=1) as response:
            if args.verbose:
                print(response.status)
    except Exception as exc:  # the Adjutant is optional; a failed ping must never break the caller
        if args.verbose:
            print(f"adjutant not reachable: {exc}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
