"""Summarize exported full-demo evidence; never opens credentials or a live ledger."""
import argparse
import collections
import hashlib
import json
from pathlib import Path
import zipfile


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def digest(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def payload(event):
    return json.loads(event["dataJson"])


def count_repeated(values):
    return sum(max(0, n - 1) for n in collections.Counter(values).values())


def ratio(numerator, denominator):
    return {"numerator": numerator, "denominator": denominator,
            "fraction": numerator / denominator if denominator else None}


def unique_events(events):
    result = {}
    for event in events:
        key = (event["branchId"], event["sequence"])
        if key in result and result[key] != event:
            raise ValueError("Conflicting world event identity: " + str(key))
        result[key] = event
    return sorted(result.values(), key=lambda event: event["sequence"])


def load_scenario(report_path, artifact_root):
    report = read_json(report_path)
    packages, files, records = [], [], []
    for branch in report.get("branches", []):
        archive = Path(branch["export"])
        if not archive.is_file() or not archive.is_relative_to(artifact_root):
            candidates = list(artifact_root.rglob("*" + branch["branchId"] + ".zip"))
            if len(candidates) != 1:
                raise ValueError("Cannot locate unique exported archive: " + branch["branchId"])
            archive = candidates[0]
        checksum = digest(archive)
        if checksum != branch["sha256"]:
            raise ValueError("Archive hash differs from host report: " + branch["branchId"])
        with zipfile.ZipFile(archive) as z:
            package = json.loads(z.read("replay.json"))
            packages.append(package)
            for raw in z.read("events.jsonl").decode("utf-8-sig").splitlines():
                # Replay checkpoints are retained in the ZIP and verified by the replay CLI.
                if any('"kind":"' + kind + '"' in raw for kind in
                       ("model_dispatched", "model_arrived", "retired_model_completion",
                        "model_response_arrived", "budget_admission", "dispatch_rejected",
                        "player_dialogue_dispatch_rejected", "full_demo_save_load_completed")):
                    records.append(json.loads(raw))
            files.append({"path": archive.relative_to(artifact_root).as_posix(), "sha256": checksum,
                          "archiveBytes": archive.stat().st_size,
                          "packageBytes": z.getinfo("replay.json").file_size,
                          "journalBytes": z.getinfo("events.jsonl").file_size,
                          "branchId": branch["branchId"], "boundaries": package["boundaryCount"],
                          "parentBoundaries": branch["parentBoundaries"]})
    completions = {c["dispatch"]["attemptId"]: c for c in report.get("completions", [])}
    pre_send, arrivals, rejected, admissions = {}, {}, [], []
    for record in records:
        kind, data = record["kind"], record["data"]
        if kind in ("model_arrived", "retired_model_completion"):
            completions.setdefault(data["dispatch"]["attemptId"], data)
        elif kind == "model_dispatched":
            pre_send[data["attemptId"]] = data
        elif kind == "model_response_arrived":
            arrivals[data["dispatch"]["attemptId"]] = data
        elif kind in ("dispatch_rejected", "player_dialogue_dispatch_rejected"):
            rejected.append(data)
        elif kind == "budget_admission":
            admissions.append(data)
    for attempt, dispatch in pre_send.items():
        if attempt not in completions:
            arrival = arrivals.get(attempt)
            completions[attempt] = {"dispatch": dispatch, "failureCode": "completion_missing",
                                    "arrivalRecordedWithoutCompletion": arrival is not None}
    return {"reportPath": str(report_path), "report": report, "packages": packages,
            "files": files, "records": records, "completions": completions,
            "preSend": pre_send, "arrivals": arrivals, "rejected": rejected, "admissions": admissions}


def summarize(scenario, parses):
    report, packages = scenario["report"], scenario["packages"]
    all_events = unique_events(report.get("events", []))
    leaf = packages[-1] if packages else None
    cutoffs, chain, root = {}, [], leaf
    while root:
        chain.append(root["initialSnapshot"]["state"]["branchId"])
        parent = root.get("parent")
        if not parent:
            break
        state = parent["savedSnapshot"]["state"]
        cutoffs[state["branchId"]] = state["sequence"]
        root = parent["prefix"]
    active = [e for e in all_events if e["branchId"] in chain
              and e["sequence"] <= cutoffs.get(e["branchId"], float("inf"))]
    active_ids = {(e["branchId"], e["sequence"]) for e in active}
    retired = [e for e in all_events if (e["branchId"], e["sequence"]) not in active_ids]
    outcomes = collections.defaultdict(list)
    for event in active:
        if event.get("logicalRequestId"):
            outcomes[event["logicalRequestId"]].append(event)
    assets = {r["id"]: {"coins": r["coins"], "inventory": dict(r["inventory"])}
              for r in root["initialSnapshot"]["state"]["residents"]} if root else {}
    initial_assets = json.loads(json.dumps(assets))
    before_outcomes = {}
    transfers = []
    for event in active:
        if event["kind"] == "resource_transferred":
            t = payload(event)
            transfers.append(t)
            seller, buyer, resource = assets[t["from"]], assets[t["to"]], t["offerResourceId"]
            seller["inventory"][resource] = seller["inventory"].get(resource, 0) - t["offerQuantity"]
            buyer["inventory"][resource] = buyer["inventory"].get(resource, 0) + t["offerQuantity"]
            seller["coins"] += t["offerPrice"]
            buyer["coins"] -= t["offerPrice"]
        elif event["kind"] in ("candidate_executed", "candidate_rejected"):
            before_outcomes[(event["branchId"], event["sequence"])] = json.loads(json.dumps(assets[event["residentId"]]))
    samples = []
    for attempt, completion in scenario["completions"].items():
        dispatch = completion["dispatch"]
        request, identity = dispatch["request"], dispatch["request"]["identity"]
        complete = (completion.get("dispatchedAtUtc") is not None
                    and completion.get("arrivedAtUtc") is not None
                    and 200 <= (completion.get("httpStatus") or 0) < 300
                    and completion.get("generationEnded") is True)
        parsed = complete and completion.get("finishReason") == "stop" and parses.get(attempt, {}).get("passed") is True
        logical = identity["logicalRequestId"]
        frozen = json.loads(json.loads(request["inputJson"])["messages"][-1]["content"])
        frozen_context = {k: frozen.get(k) for k in ("resident", "allowedActions", "allowedTargets", "commitment", "candidateFields", "rules")}
        samples.append({"scenario": report["scenario"], "attemptId": attempt, **identity,
                        "httpDispatchedConfirmed": completion.get("dispatchedAtUtc") is not None or attempt in scenario["arrivals"],
                        "stage": {0: "baseline", 1: "counterfactual", 2: "player_dialogue", 3: "manual"}.get(request["stage"], request["stage"]),
                        "recovery": request["isRecovery"],
                        "inputFingerprint": dispatch["inputFingerprint"],
                        "reservedAtUtc": dispatch["reservedAtUtc"],
                        **{k: completion.get(k) for k in ("dispatchedAtUtc", "arrivedAtUtc", "httpStatus",
                           "providerRequestId", "returnedModel", "systemFingerprint", "finishReason", "elapsed",
                           "logicalElapsedToArrival", "usage", "failureCode", "generationEnded", "budgetSettled",
                           "slotHeld", "conservativeCostMicros")},
                        "transportComplete": complete, "protocolParsed": parsed,
                        "protocolParse": parses.get(attempt),
                        "frozenContext": frozen_context,
                        "worldOutcomes": [{**{k: e[k] for k in ("branchId", "sequence", "worldMinute", "kind", "source", "reason")},
                                           "residentAssetsAtOutcome": before_outcomes.get((e["branchId"], e["sequence"]))}
                                          for e in outcomes.get(logical, [])
                                          if e["kind"] not in ("time_input", "decision_created", "decision_dispatched")],
                        "candidateJson": completion.get("candidateJson")})
    source = "real_model" if report["mode"] == "real" else "rules"
    npc = [e for e in active if e["kind"] == "candidate_executed" and e["source"] == source]
    player_source = "real_model" if report["mode"] == "real" else "fixed_model"
    players = [e for e in active if e["kind"] == "player_dialogue_displayed" and e["source"] == player_source
               and payload(e)["display"]["error"] is None]
    metric_groups = {}
    for kind, adopted in (("npc_action", npc), ("player_text", players), ("total", npc + players)):
        rows = [s for s in samples if kind == "total" or s["kind"] == kind]
        sends = sum(s["httpDispatchedConfirmed"] for s in rows)
        complete = sum(s["transportComplete"] for s in rows)
        parsed = sum(s["protocolParsed"] for s in rows)
        parsed_logical = {s["logicalRequestId"] for s in rows if s["protocolParsed"]}
        adopted_logical = {e["logicalRequestId"] for e in adopted}
        metric_groups[kind] = {"httpAttempts": sends, "completeResponses": complete, "parsedResponses": parsed,
                               "parsedLogicalRequests": len(parsed_logical), "adoptedLogicalRequests": len(adopted_logical),
                               "transmission": ratio(complete, sends), "structure": ratio(parsed, complete),
                               "legalAdoption": ratio(len(adopted_logical & parsed_logical), len(parsed_logical))}
    windows = []
    for window in report.get("windows", []):
        coverage = {resident: sum(e["residentId"] == resident and window["startMinute"] <= e["worldMinute"] <= window["endMinute"]
                                 for e in npc) for resident in ("mina", "eli", "ren", "sora")}
        windows.append({**window, "recomputedCoverage": coverage,
                        "coverageAgrees": all(c["adopted"] == coverage[c["residentId"]] for c in window["coverage"])})
    opened = {e["activityId"]: e for e in active if e["kind"] == "player_dialogue_opened"}
    requested = {e["logicalRequestId"]: e for e in active if e["kind"] == "player_dialogue_requested"}
    wrong_sessions = []
    for e in players:
        session, request = opened.get(e["activityId"]), requested.get(e["logicalRequestId"])
        physical = [s for s in samples if s["logicalRequestId"] == e["logicalRequestId"] and s["protocolParsed"]]
        if not session or not request or session["residentId"] != e["residentId"] or request["residentId"] != e["residentId"] \
                or request["activityId"] != e["activityId"] or report["mode"] == "real" and not any(
                    s["residentId"] == e["residentId"] and s["playerSessionId"] == e["activityId"] for s in physical):
            wrong_sessions.append(e)
    final_state = json.loads(report["finalState"]["worldJson"])["state"] if report.get("finalState") else None
    asset_checks = []
    if final_state:
        for resident in final_state["residents"]:
            actual = {"coins": resident["coins"], "inventory": resident["inventory"]}
            asset_checks.append({"residentId": resident["id"], "reconstructed": assets[resident["id"]],
                                 "final": actual, "passed": actual == assets[resident["id"]]})
    memory_ids = [payload(e)["id"] for e in active if e["kind"] == "player_dialogue_memory_committed"]
    uncertain = [attempt for attempt in scenario["preSend"]
                 if scenario["completions"][attempt].get("failureCode") == "completion_missing" and attempt not in scenario["arrivals"]]
    duplicate_resident_memories = sum(count_repeated((m["sequence"], m["source"], m["kind"], m["text"]) for m in r["memories"])
                                      for r in final_state["residents"]) if final_state else None
    checks = {
        "hostFlowComplete": report.get("passed") is True,
        "twoDailyWindows": len(windows) == 2,
        "windowLimits": len(windows) == 2 and all(w["elapsedSeconds"] <= 180 and w["ordinaryAdmissions"] <= 18 for w in windows),
        "dailyFourResidentCoverage": len(windows) == 2 and all(all(n >= 1 for n in w["recomputedCoverage"].values()) for w in windows),
        "hostCoverageAgrees": len(windows) == 2 and all(w["coverageAgrees"] for w in windows),
        "playerFourResidentCoverage": {e["residentId"] for e in players} == {"mina", "eli", "ren", "sora"},
        "twentyOneCommands": len(report.get("commands", [])) == 21,
        "oneMorningTwoSleeps": report.get("mornings") == 1 and report.get("sleeps") == 2,
        "saveRestore": any(r["kind"] == "full_demo_save_load_completed" for r in scenario["records"]),
        "noDuplicateExecution": count_repeated(e["logicalRequestId"] for e in active if e["kind"] == "candidate_executed") == 0,
        "noDuplicateTransfer": count_repeated(t["transferId"] for t in transfers) == 0,
        "noDuplicatePlayerMemory": count_repeated(memory_ids) == 0,
        "noDuplicateResidentMemory": duplicate_resident_memories == 0,
        "correctPlayerSessions": not wrong_sessions,
        "residentAssetsMatchTransfers": len(asset_checks) == 4 and all(c["passed"] for c in asset_checks),
        "noUncertainDispatch": not uncertain,
    }
    if report["mode"] == "real":
        for kind, group in metric_groups.items():
            for metric, threshold in (("transmission", .9), ("structure", .9), ("legalAdoption", .8)):
                fraction = group[metric]["fraction"]
                checks[kind + "." + metric] = fraction is not None and fraction >= threshold
    return {"scenario": report["scenario"], "mode": report["mode"], "passed": all(checks.values()), "checks": checks,
            "error": report.get("error"), "acceptedRealSeconds": report["acceptedRealSeconds"], "wallSeconds": report["wallSeconds"],
            "inputSha256": report.get("inputSha256"), "toolchain": report.get("toolchain"),
            "activeBranchChain": chain, "ancestorSequenceLimits": cutoffs,
            "activeWorldEvents": len(active), "retiredWorldEvents": len(retired), "metrics": metric_groups,
            "windows": windows, "playerCoverage": dict(collections.Counter(e["residentId"] for e in players)),
            "worldCounts": dict(collections.Counter(e["kind"] for e in active)),
            "modelActions": dict(collections.Counter(e["reason"] for e in npc)),
            "candidateRejections": dict(collections.Counter(e["reason"] for e in active if e["kind"] == "candidate_rejected")),
            "responseRejections": dict(collections.Counter(e["reason"] for e in active if e["kind"] in ("response_rejected", "player_dialogue_response_rejected"))),
            "ruleFallbacks": dict(collections.Counter(e["reason"] for e in active if e["kind"] == "rule_fallback")),
            "uncertainDispatchAttempts": uncertain, "preSendRejections": scenario["rejected"],
            "budgetAdmissions": {key: n for key, n in collections.Counter(a["admission"] for a in scenario["admissions"]).items()},
            "initialResidentAssets": initial_assets, "residentAssets": asset_checks, "transfers": transfers, "wrongPlayerSessions": wrong_sessions,
            "duplicateResidentMemories": duplicate_resident_memories,
            "exports": scenario["files"], "samples": samples}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifact-root", type=Path, required=True)
    parser.add_argument("--reports", type=Path, nargs="+", required=True)
    parser.add_argument("--protocol-input", type=Path, required=True)
    parser.add_argument("--protocol-results", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    scenarios = [load_scenario(p, args.artifact_root.resolve()) for p in args.reports]
    protocol_input = [{"attemptId": attempt, "kind": c["dispatch"]["request"]["identity"]["kind"],
                       "candidateJson": c.get("candidateJson")} for s in scenarios for attempt, c in s["completions"].items()]
    write_json(args.protocol_input, protocol_input)
    if args.output is None:
        return
    parses = {p["attemptId"]: p for p in read_json(args.protocol_results)} if args.protocol_results else {}
    result = {"schemaVersion": 1,
              "scope": "Exported host reports and complete ZIPs; no provider calls, credential reads or live ledger access.",
              "costMeaning": "Conservative CNY micro-unit deduction at registered peak all-input-miss rates; not provider invoice.",
              "scenarios": [summarize(s, parses) for s in scenarios]}
    freeze_path = args.artifact_root / "freeze.json"
    if freeze_path.is_file():
        freeze = read_json(freeze_path)
        result["freeze"] = freeze
        result["freezeChecks"] = {"canonicalInput": all(s["report"].get("inputSha256") == freeze["canonicalInputSha256"] for s in scenarios),
                                  "sourceDigest": all(s["report"].get("toolchain", {}).get("scene", {}).get("simulationSourceDigest") == freeze["simulationSourceDigest"] for s in scenarios),
                                  "buildCommit": all(s["report"].get("toolchain", {}).get("scene", {}).get("buildVersion", "").endswith(freeze["sourceCommit"]) for s in scenarios)}
    if len(scenarios) == 2 and all(s["packages"] for s in scenarios):
        roots = []
        for s in scenarios:
            p = s["packages"][-1]
            while p.get("parent"):
                p = p["parent"]["prefix"]
            roots.append(p)
        left, right = roots
        normalize = lambda definitions: {**definitions, "residents": [{k: v for k, v in r.items() if k != "inventory"} for r in definitions["residents"]]}
        totals = lambda p: {k: sum(r["inventory"].get(k, 0) for r in p["initialSnapshot"]["state"]["residents"])
                            for k in {key for r in p["initialSnapshot"]["state"]["residents"] for key in r["inventory"]}}
        result["counterfactualChecks"] = {"sameManifest": left["manifest"] == right["manifest"],
                                           "sameDefinitionsExceptResidentInventory": normalize(left["definitions"]) == normalize(right["definitions"]),
                                           "sameInitialResourceTotals": totals(left) == totals(right),
                                           "sameInitialResidentsExceptInventory": all({k: v for k, v in a.items() if k != "inventory"} == {k: v for k, v in b.items() if k != "inventory"}
                                                for a, b in zip(left["initialSnapshot"]["state"]["residents"], right["initialSnapshot"]["state"]["residents"]))}
    result["metrics"] = {}
    for kind in ("npc_action", "player_text", "total"):
        totals = {k: sum(s["metrics"][kind][k] for s in result["scenarios"])
                  for k in ("httpAttempts", "completeResponses", "parsedResponses", "parsedLogicalRequests", "adoptedLogicalRequests")}
        totals["transmission"] = ratio(totals["completeResponses"], totals["httpAttempts"])
        totals["structure"] = ratio(totals["parsedResponses"], totals["completeResponses"])
        totals["legalAdoption"] = ratio(totals["adoptedLogicalRequests"], totals["parsedLogicalRequests"])
        result["metrics"][kind] = totals
    snapshots = [s["report"]["budget"] for s in scenarios if s["report"].get("budget")]
    if snapshots:
        final = max(snapshots, key=lambda b: b["totalAttempts"])
        result["finalBudget"] = final
        result["budgetChecks"] = {"sharedIdentity": len({b["budgetId"] for b in snapshots}) == 1,
                                  "withinAmount": final["settledMicros"] + final["reservedMicros"] <= 5000000,
                                  "withinTotalAttempts": final["totalAttempts"] <= 96,
                                  "manualUntouched": not any(a["stage"] == "manual" for a in final["attempts"]),
                                  "slotsClear": final["slotsHeld"] == 0,
                                  "noReservedAmount": final["reservedMicros"] == 0,
                                  "noBudgetStop": final["stopReason"] is None,
                                  "priorAttemptsPreserved": all(a in final["attempts"] for b in snapshots for a in b["attempts"]),
                                  "withinStages": all(sum(a["stage"] == stage for a in final["attempts"]) <= limit
                                                      for stage, limit in (("baseline", 40), ("counterfactual", 40), ("player_dialogue", 8), ("manual", 8)))}
        result["stages"] = dict(collections.Counter(a["stage"] for a in final["attempts"]))
        attempts = {a["attemptId"]: a for a in final["attempts"]}
        result["perScenarioBudget"] = [{"scenario": s["report"]["scenario"],
                                       "budgetAttempts": len(s["completions"]),
                                       "settledMicros": sum(attempts[a]["settledCostMicros"] for a in s["completions"]),
                                       "reservedMicros": sum(attempts[a]["reservedCostMicros"] for a in s["completions"])}
                                      for s in scenarios]
        result["unknownAttempts"] = [a for a in final["attempts"] if a["status"] == "unknown" or a["slotHeld"] or a["reservedCostMicros"]]
    result["passed"] = len(scenarios) == 2 and all(s["passed"] for s in result["scenarios"]) \
        and all(result.get("budgetChecks", {}).values()) and all(result.get("freezeChecks", {}).values()) \
        and all(result.get("counterfactualChecks", {}).values())
    write_json(args.output, result)


if __name__ == "__main__":
    main()
