#!/usr/bin/env python3
# Star history chart (light and dark SVG) for the README, no dependencies.
# Port of the star-history chart code (MIT, github.com/star-history/star-history).
# Usage: GITHUB_TOKEN=... star-history.py owner/repo output-dir
import base64
import json
import math
import os
import re
import sys
import urllib.request
from datetime import datetime, timedelta, timezone

API = "https://api.github.com"
PER_PAGE = 100
MAX_PAGES = 16
WIDTH = 800
HEIGHT = WIDTH * 2 / 3
TOP, RIGHT, BOTTOM, LEFT = 60, 30, 50, 70
FONT = "xkcd, &quot;Comic Neue&quot;, &quot;Chalkboard SE&quot;, &quot;Comic Sans MS&quot;, sans-serif"
# Text widths in Comic Neue, as measured by the action.
TITLE_WIDTH = 103.2
LEGEND_CHAR_WIDTH = 6.5
THEMES = {
    "light": {"bg": "#fff", "fg": "#000", "line": "#dd4528"},
    "dark": {"bg": "#0d1117", "fg": "#fff", "line": "#ff6b6b"},
}
DAY = 86400
TIME_INTERVALS = [("day", 1, DAY), ("day", 2, 2 * DAY), ("week", 1, 7 * DAY),
                  ("month", 1, 30 * DAY), ("month", 3, 90 * DAY), ("year", 1, 365 * DAY)]


def get(url, token=None, accept="application/vnd.github.v3.star+json"):
    headers = {"Accept": accept}
    if token:
        headers["Authorization"] = f"token {token}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=15) as res:
        return res.read(), res.headers


def get_json(url, token):
    body, headers = get(url, token)
    return json.loads(body), headers


def parse_time(s):
    return datetime.strptime(s, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)


def star_records(repo, token):
    url = f"{API}/repos/{repo}/stargazers?per_page={PER_PAGE}"
    first, headers = get_json(url, token)
    m = re.search(r'[?&]page=(\d+)>; rel="last"', headers.get("Link", ""))
    pages = int(m.group(1)) if m else 1
    if not first:
        sys.exit(f"{repo} has no stars")

    records = {}
    if pages < MAX_PAGES:
        stars = list(first)
        for p in range(2, pages + 1):
            stars += get_json(f"{url}&page={p}", token)[0]
        step = len(stars) // MAX_PAGES or 1
        for i in range(0, len(stars), step):
            records[parse_time(stars[i]["starred_at"])] = i + 1
    else:
        wanted = [round(i * pages / MAX_PAGES) - 1 for i in range(1, MAX_PAGES + 1)]
        if 1 not in wanted:
            wanted[0] = 1
        for p in wanted:
            data = first if p == 1 else get_json(f"{url}&page={p}", token)[0]
            if data:
                records[parse_time(data[0]["starred_at"])] = PER_PAGE * (p - 1)

    now = datetime.now(timezone.utc).replace(microsecond=0)
    records[now] = get_json(f"{API}/repos/{repo}", token)[0]["stargazers_count"]
    return list(records.items())


def avatar(repo, token):
    url = get_json(f"{API}/users/{repo.split('/')[0]}", token)[0]["avatar_url"]
    body, headers = get(url, accept="image/*")
    ctype = headers.get("Content-Type", "")
    if not ctype.startswith("image/") or len(body) > 2 * 1024 * 1024:
        return ""
    return f"data:{ctype};base64,{base64.b64encode(body).decode()}"


def num(v):
    s = f"{v:.3f}".rstrip("0").rstrip(".")
    return "0" if s == "-0" else s


def esc(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def floor_time(t, unit):
    t = t.replace(hour=0, minute=0, second=0, microsecond=0)
    if unit == "week":
        return t - timedelta(days=(t.weekday() + 1) % 7)
    if unit == "month":
        return t.replace(day=1)
    if unit == "year":
        return t.replace(month=1, day=1)
    return t


def add_time(t, unit, n):
    if unit == "day":
        return t + timedelta(days=n)
    if unit == "week":
        return t + timedelta(weeks=n)
    if unit == "month":
        m = t.month - 1 + n
        return t.replace(year=t.year + m // 12, month=m % 12 + 1)
    return t.replace(year=t.year + n)


def number_step(start, stop, count):
    step = (stop - start) / count
    power = max(0, math.floor(math.log10(step)))
    error = step / 10 ** power
    factor = 10 if error >= math.sqrt(50) else 5 if error >= math.sqrt(10) else 2 if error >= math.sqrt(2) else 1
    return 10 ** power * factor


def number_ticks(start, stop, count):
    inc = number_step(start, stop, count)
    i1, i2 = round(start / inc), round(stop / inc)
    if i1 * inc < start:
        i1 += 1
    if i2 * inc > stop:
        i2 -= 1
    return [i * inc for i in range(i1, i2 + 1)]


def time_ticks(start, stop, count):
    target = (stop - start).total_seconds() / count
    i = next((k for k, iv in enumerate(TIME_INTERVALS) if iv[2] > target), len(TIME_INTERVALS))
    if i == len(TIME_INTERVALS):
        unit, step = "year", number_step(0, (stop - start).total_seconds() / (365 * DAY), count)
    elif i == 0:
        unit, step = "day", 1
    else:
        prev, cur = TIME_INTERVALS[i - 1], TIME_INTERVALS[i]
        unit, step = (prev if target / prev[2] < cur[2] / target else cur)[:2]

    t = floor_time(start, unit)
    if t < start:
        t = add_time(t, unit, 1)
    ticks = []
    while t <= stop:
        field = {"day": t.day - 1, "month": t.month - 1, "year": t.year}.get(unit, 0)
        if field % step == 0:
            ticks.append(t)
        t = add_time(t, unit, 1)
    return ticks


def time_label(t):
    if floor_time(t, "month") < t:
        return t.strftime("%a %d") if floor_time(t, "week") < t else t.strftime("%b %d")
    if floor_time(t, "year") < t:
        return t.strftime("%B")
    return t.strftime("%Y")


def number_label(v, unit):
    if unit == 1:
        return f"{v}"
    if unit == 1000000:
        return f"{v // 1000000}M" if v % 1000000 == 0 else f"{v / 1000000:.1f}M"
    return f"{v // 1000}K" if v % 1000 == 0 else f"{v / 1000:.1f}K"


def monotone_path(points):
    # d3-shape curveMonotoneX
    def slope3(x0, y0, x1, y1, x2, y2):
        h0, h1 = x1 - x0, x2 - x1
        s0, s1 = (y1 - y0) / h0, (y2 - y1) / h1
        p = (s0 * h1 + s1 * h0) / (h0 + h1)
        sign = lambda v: -1 if v < 0 else 1
        return (sign(s0) + sign(s1)) * min(abs(s0), abs(s1), 0.5 * abs(p))

    def slope2(x0, y0, x1, y1, t):
        h = x1 - x0
        return (3 * (y1 - y0) / h - t) / 2 if h else t

    def curve(x0, y0, x1, y1, t0, t1):
        dx = (x1 - x0) / 3
        return f"C{num(x0 + dx)},{num(y0 + dx * t0)},{num(x1 - dx)},{num(y1 - dx * t1)},{num(x1)},{num(y1)}"

    pts = []
    for p in points:
        if not pts or p != pts[-1]:
            pts.append(p)
    d = f"M{num(pts[0][0])},{num(pts[0][1])}"
    if len(pts) == 2:
        return d + f"L{num(pts[1][0])},{num(pts[1][1])}"
    t0 = None
    for k in range(2, len(pts)):
        (x0, y0), (x1, y1), (x2, y2) = pts[k - 2], pts[k - 1], pts[k]
        t1 = slope3(x0, y0, x1, y1, x2, y2)
        d += curve(x0, y0, x1, y1, slope2(x0, y0, x1, y1, t1) if t0 is None else t0, t1)
        t0 = t1
    (x0, y0), (x1, y1) = pts[-2], pts[-1]
    return d + curve(x0, y0, x1, y1, t0, slope2(x0, y0, x1, y1, t0))


def render(repo, records, logo, theme):
    c = THEMES[theme]
    fg = c["fg"]
    pts = list(records)
    if pts[0][1] > 0:
        pts.insert(0, (pts[0][0] - timedelta(days=1), 0))

    cw, ch = WIDTH - LEFT - RIGHT, HEIGHT - TOP - BOTTOM
    t0, t1 = min(t for t, _ in pts), max(t for t, _ in pts)
    ymax = max(v for _, v in pts)
    xs = lambda t: (t - t0).total_seconds() / (t1 - t0).total_seconds() * cw
    ys = lambda v: ch - v / ymax * ch
    tick = f"font-family:{FONT};font-size:16px;fill:{fg}"

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{num(HEIGHT)}" '
           f'style="stroke-width:3;font-family:{FONT};background:{c["bg"]}">',
           '<filter id="xkcdify" filterUnits="userSpaceOnUse" x="-5" y="-5" width="100%" height="100%">'
           '<feTurbulence type="fractalNoise" baseFrequency="0.05" result="noise"/>'
           '<feDisplacementMap scale="5" xChannelSelector="R" yChannelSelector="G" in="SourceGraphic" in2="noise"/>'
           '</filter>',
           f'<g pointer-events="all" transform="translate({LEFT},{TOP})">']

    out.append(f'<g class="xaxis" fill="none" font-family="sans-serif" font-size="10" text-anchor="middle" transform="translate(0,{num(ch)})">'
               f'<path class="domain" stroke="currentColor" d="M0.5,0.5H{num(cw + 0.5)}" filter="url(#xkcdify)" style="stroke:{fg}"/>')
    for t in time_ticks(t0, t1, 5):
        out.append(f'<text fill="currentColor" y="6" dy="0.71em" transform="translate({num(xs(t) + 0.5)},0)" style="{tick}">{time_label(t)}</text>')
    out.append("</g>")

    out.append(f'<g class="yaxis" fill="none" font-family="sans-serif" font-size="10" text-anchor="end">'
               f'<path class="domain" stroke="currentColor" d="M-1,{num(ch + 0.5)}H0.5V0.5H-1" filter="url(#xkcdify)" style="stroke:{fg}"/>')
    unit = None
    for v in number_ticks(0, ymax, 5):
        y = num(ys(v) + 0.5)
        if v == 0:
            label = " "
        else:
            unit = unit or (1000000 if v >= 1000000 else 1000 if v >= 300 else 1)
            label = number_label(v, unit)
        out.append(f'<g class="tick" transform="translate(0,{y})"><path stroke="currentColor" d="M0,0H-1"/>'
                   f'<text fill="currentColor" x="-7" dy="0.32em" style="{tick}">{label}</text></g>')
    out.append("</g>")

    out.append(f'<path fill="none" stroke="{c["line"]}" filter="url(#xkcdify)" d="{monotone_path([(xs(t), ys(v)) for t, v in pts])}"/>')

    label_width = num(len(repo) * LEGEND_CHAR_WIDTH)
    out.append(f'<rect x="8" y="5" width="{num(21 + len(repo) * LEGEND_CHAR_WIDTH + 7)}" height="32" rx="5" ry="5" '
               f'fill-opacity="0.85" stroke="{fg}" stroke-width="2" filter="url(#xkcdify)" style="fill:{c["bg"]}"/>'
               f'<rect x="15" y="17" width="8" height="8" rx="2" ry="2" filter="url(#xkcdify)" style="fill:{c["line"]}"/>'
               f'<text x="29" y="25" textLength="{label_width}" lengthAdjust="spacingAndGlyphs" style="font-size:15px;fill:{fg}">{esc(repo)}</text>')
    out.append("</g>")

    out.append(f'<text x="50%" y="30" text-anchor="middle" textLength="{TITLE_WIDTH}" lengthAdjust="spacingAndGlyphs" '
               f'style="font-size:20px;font-weight:700;fill:{fg}">Star History</text>')
    if logo:
        out.append(f'<clipPath id="clip-circle-title"><circle r="11" cx="{num(WIDTH / 2 - 73)}" cy="23"/></clipPath>'
                   f'<image x="{num(WIDTH / 2 - 84)}" y="12" width="22" height="22" href="{logo}" clip-path="url(#clip-circle-title)"/>')
    out.append(f'<text x="50%" y="{num(HEIGHT - 10)}" text-anchor="middle" style="font-size:17px;fill:{fg}">Date</text>')
    offset = 2 if ymax > 100000 else 8 if ymax > 10000 else 12 if ymax > 1000 else 20 if ymax > 100 else 24
    out.append(f'<text x="{math.floor(50 - HEIGHT / 2)}" y="{offset}" dy="0.75em" text-anchor="end" transform="rotate(-90)" '
               f'style="font-size:17px;fill:{fg}">GitHub Stars</text>')
    out.append("</svg>\n")
    return "".join(out)


def main():
    repo, out_dir = sys.argv[1], sys.argv[2]
    token = os.environ["GITHUB_TOKEN"]
    records = star_records(repo, token)
    logo = avatar(repo, token)
    os.makedirs(out_dir, exist_ok=True)
    for theme in THEMES:
        path = os.path.join(out_dir, f"star-history-{theme}.svg")
        with open(path, "w", encoding="utf-8") as f:
            f.write(render(repo, records, logo, theme))
        print(f"Wrote {path}")


if __name__ == "__main__":
    main()
