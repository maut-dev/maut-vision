import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeChecksChanged } from "@/api/events/subscriptions";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { LiveCheckFrameDto, LiveCheckMode, LiveCheckResponseDto } from "@/api/projects/checks/response-dto/live-check-response-dto";
import { isLiveCheckActive } from "./checks/check-view";

/** The journal page an event reads up to. */
const journalPage = 200;

/**
 * A live check of one mode of Checks without polling: a snapshot from the answer of start* or checks.get, then checks.changed of this check,
 * applied only when sequence is greater than the known one. New journal rows (frames with detections) are read with listJournal with
 * after when an event reports lastOrdinal greater than the loaded one; memory keeps at most the last keep rows, earlier ones are read by
 * olderJournal (before). On mount the hook finds the latest check of its mode (checks.list → get): moving between tabs
 * and sections does not lose a running check. stopOnLeave — a screen check: leaving the section stops it.
 */
export function useLiveCheck(projectId: string, mode: LiveCheckMode, { keep, stopOnLeave = false }: { keep: number; stopOnLeave?: boolean }) {
    const [check, setCheck] = useState<LiveCheckResponseDto>();
    const [journal, setJournal] = useState<readonly LiveCheckFrameDto[]>([]);
    const [loadingOlder, setLoadingOlder] = useState(false);
    const current = useRef<LiveCheckResponseDto | undefined>(undefined);
    const lastOrdinal = useRef(0);
    const reading = useRef(false);
    const mounted = useRef(true);

    /** Rows by ascending ordinal without repeats, at most the last keep. */
    const merge = useCallback((rows: readonly LiveCheckFrameDto[], older = false) => {
        setJournal(known => {
            const byOrdinal = new Map(known.map(row => [row.ordinal, row]));
            for (const row of rows) if (row.ordinal > 0) byOrdinal.set(row.ordinal, row);
            const sorted = [...byOrdinal.values()].sort((left, right) => left.ordinal - right.ordinal);
            return older ? sorted : sorted.slice(-keep);
        });
        for (const row of rows) lastOrdinal.current = Math.max(lastOrdinal.current, row.ordinal);
    }, [keep]);

    const follow = useCallback(async (snapshot: LiveCheckResponseDto) => {
        if (reading.current) return;
        reading.current = true;
        try {
            while (mounted.current && current.current?.id === snapshot.id && current.current.journal.lastOrdinal > lastOrdinal.current) {
                // Memory keeps only the last keep rows: what was skipped earlier is read by olderJournal.
                lastOrdinal.current = Math.max(lastOrdinal.current, current.current.journal.lastOrdinal - keep);
                const page = await checksApi.listJournal({ projectId, checkId: snapshot.id, after: lastOrdinal.current, limit: Math.min(journalPage, Math.max(keep, 1)) });
                if (!mounted.current || current.current?.id !== snapshot.id) return;
                if (!page.items.length) {
                    // The screen ring pushed out the requested records: continue from the first stored one.
                    lastOrdinal.current = Math.max(lastOrdinal.current, page.journal.firstOrdinal - 1);
                    if (page.journal.lastOrdinal <= lastOrdinal.current) break;
                    continue;
                }
                merge(page.items);
            }
        } catch {
            // The next event of the check repeats the reading.
        } finally {
            reading.current = false;
        }
    }, [projectId, keep, merge]);

    const apply = useCallback((snapshot: LiveCheckResponseDto) => {
        const known = current.current;
        if (!known || known.id !== snapshot.id || snapshot.sequence <= known.sequence) return;
        current.current = snapshot;
        setCheck(snapshot);
        if (snapshot.journal.lastOrdinal > lastOrdinal.current) void follow(snapshot);
    }, [follow]);

    /** Show a check: the answer of start* or a get snapshot (recentFrames is the last page of the journal). */
    const attach = useCallback((snapshot: LiveCheckResponseDto) => {
        if (current.current?.id === snapshot.id) {
            apply(snapshot);
            return;
        }
        current.current = snapshot;
        lastOrdinal.current = 0;
        setJournal([]);
        setCheck(snapshot);
        merge(snapshot.recentFrames);
        if (snapshot.journal.lastOrdinal > lastOrdinal.current) void follow(snapshot);
    }, [apply, follow, merge]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeChecksChanged(event => {
            if (event.data.projectId === projectId && event.data.check) apply(event.data.check);
        });
        (async () => {
            try {
                const list = await checksApi.list({ projectId });
                const latest = list.filter(item => item.mode === mode)
                    .sort((left, right) => right.startedAt.localeCompare(left.startedAt) || right.sequence - left.sequence)[0];
                if (!latest || !mounted.current || current.current) return;
                const snapshot = await checksApi.get({ projectId, checkId: latest.id, journalLimit: Math.min(keep, 200) });
                if (mounted.current && !current.current) attach(snapshot);
            } catch {
                // There is no earlier check: the mode starts with an empty form.
            }
        })();
        return () => {
            mounted.current = false;
            unsubscribe();
            const running = current.current;
            if (stopOnLeave && running && isLiveCheckActive(running.state)) void checksApi.stop({ projectId, checkId: running.id }).catch(() => undefined);
        };
    }, [projectId, mode, keep, stopOnLeave, apply, attach]);

    /** Stop. */
    const stop = useCallback(async () => {
        const running = current.current;
        if (!running) return;
        apply(await checksApi.stop({ projectId, checkId: running.id }));
    }, [projectId, apply]);

    /** Earlier journal rows before the first loaded one. */
    const olderJournal = useCallback(async () => {
        const running = current.current;
        const first = journal[0]?.ordinal;
        if (!running || !first) return;
        setLoadingOlder(true);
        try {
            const page = await checksApi.listJournal({ projectId, checkId: running.id, before: first, limit: journalPage });
            if (mounted.current && current.current?.id === running.id) merge(page.items, true);
        } finally {
            if (mounted.current) setLoadingOlder(false);
        }
    }, [projectId, journal, merge]);

    const active = !!check && isLiveCheckActive(check.state);
    return { check, journal, active, attach, stop, olderJournal, loadingOlder };
}

export type LiveCheckSession = ReturnType<typeof useLiveCheck>;
