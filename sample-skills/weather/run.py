#!/usr/bin/env python3
"""
AgentCli weather skill — wttr.in backend.

Called by FileSkillProvider as:
  ./run.py <toolName> '<argsJson>'

Stdout → returned to agent as tool result.
"""

import sys
import json
import urllib.request
import urllib.parse

def get_weather(args: dict) -> str:
    location = args.get("location", "")
    units    = args.get("units", "metric")

    if not location:
        return "Error: 'location' is required"

    unit_flag = "m" if units == "metric" else "u"
    encoded   = urllib.parse.quote(location)
    url       = f"https://wttr.in/{encoded}?format=4&{unit_flag}"

    try:
        req = urllib.request.Request(url, headers={"User-Agent": "AgentCli/1.0"})
        with urllib.request.urlopen(req, timeout=10) as resp:
            one_liner = resp.read().decode("utf-8").strip()

        # Also fetch the short forecast (3-day)
        url2 = f"https://wttr.in/{encoded}?format=%l:+%C+%t+(feels+%f),+humidity+%h,+wind+%w&{unit_flag}"
        req2 = urllib.request.Request(url2, headers={"User-Agent": "AgentCli/1.0"})
        with urllib.request.urlopen(req2, timeout=10) as resp2:
            detail = resp2.read().decode("utf-8").strip()

        return f"{one_liner}\n{detail}"

    except Exception as e:
        return f"Error fetching weather: {e}"


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Error: expected <toolName> <argsJson>", file=sys.stderr)
        sys.exit(1)

    tool_name = sys.argv[1]
    try:
        tool_args = json.loads(sys.argv[2])
    except json.JSONDecodeError as e:
        print(f"Error: invalid JSON args — {e}", file=sys.stderr)
        sys.exit(1)

    if tool_name == "get_weather":
        print(get_weather(tool_args))
    else:
        print(f"Error: weather skill has no tool '{tool_name}'", file=sys.stderr)
        sys.exit(1)
