"""Explore generated protocol cases, retaining seeds that reach new driver lines."""
import argparse
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "tests/ZkLink.Protocol.Tests"
SOURCES = {"ReplyCodec.cs", "JuteReader.cs", "HandshakeCodec.cs",
           "ConnectionStringParser.cs", "FrameReader.cs", "ZNodePath.cs"}


def driver_lines(report):
    covered = set()
    for cls in ET.parse(report).iter("class"):
        name = cls.get("filename", "").replace("\\", "/").split("/")[-1]
        if name in SOURCES:
            for line in cls.findall("lines/line"):
                if int(line.attrib["hits"]) > 0:
                    covered.add((name, int(line.attrib["number"])))
    if not covered:
        raise ValueError("Coverage report contained no hits in the requested decoder files")
    return covered


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cases", type=int, choices=range(1, 129), default=24, metavar="1..128")
    parser.add_argument("--seed", type=int, default=1729)
    args = parser.parse_args()
    parent = ROOT / "artifacts" / "decoder-coverage"
    parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="run-", dir=parent))
    print(f"Reports: {run}", flush=True)
    subprocess.run(["dotnet", "restore", PROJECT, "--locked-mode"], cwd=ROOT, check=True)
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "--no-restore"], cwd=ROOT, check=True)
    rng = random.Random(args.seed)
    corpus, visited, coverage = [], set(), set()
    for i in range(args.cases):
        while True:
            candidate = rng.randrange(2**31) if not corpus or i % 4 == 0 else (
                rng.choice(corpus) ^ (1 << rng.randrange(31)))
            if candidate not in visited:
                break
        visited.add(candidate)
        case = run / str(candidate)
        case.mkdir()
        print(f"Case {i + 1}/{args.cases}, seed {candidate}", flush=True)
        with (case / "test.log").open("w") as log:
            result = subprocess.run([
                "dotnet", "test", "--project", PROJECT, "-c", "Release", "--no-build",
                "--filter-class", "*DecoderPropertyTests", "--results-directory", case,
                "--coverage", "--coverage-output-format", "cobertura", "--coverage-output", "decoder.xml",
            ], env={**os.environ, "ZKLINK_DECODER_CAMPAIGN_SEED": str(candidate)},
                cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=90)
        if result.returncode:
            raise SystemExit(f"Seed {candidate} failed, see {case / 'test.log'}")
        report, = case.rglob("decoder.xml")
        found = driver_lines(report)
        if found - coverage:
            corpus.append(candidate)
            coverage.update(found)
            print(f"Retained seed {candidate}, {len(coverage)} covered decoder lines", flush=True)
    (run / "corpus.json").write_text(json.dumps({
        "campaignSeed": args.seed, "executedSeeds": sorted(visited),
        "retainedSeeds": corpus, "coveredLines": sorted(coverage)
    }, indent=2) + "\n")


if __name__ == "__main__":
    main()
