import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeChecksChanged } from "@/api/events/subscriptions";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { LiveCheckFrameDto, LiveCheckMode, LiveCheckResponseDto } from "@/api/projects/checks/response-dto/live-check-response-dto";
import { isLiveCheckActive } from "./checks/check-view";

/** Страница журнала, которую дочитывает событие. */
const journalPage = 200;

/**
 * Живая проверка одного режима «Проверок» без опроса: снимок из ответа start* или checks.get, затем checks.changed этой проверки —
 * применяется, только если sequence больше известного. Новые строки журнала («кадры с обнаружениями») дочитываются listJournal с
 * after, когда событие сообщает lastOrdinal больше загруженного; в памяти — не больше keep последних строк, более ранние читает
 * olderJournal (before). При монтировании хук находит последнюю проверку своего режима (checks.list → get): переход по вкладкам
 * и разделам не теряет идущую проверку. stopOnLeave — проверка с экрана: уход из раздела её останавливает.
 */
export function useLiveCheck(projectId: string, mode: LiveCheckMode, { keep, stopOnLeave = false }: { keep: number; stopOnLeave?: boolean }) {
    const [check, setCheck] = useState<LiveCheckResponseDto>();
    const [journal, setJournal] = useState<readonly LiveCheckFrameDto[]>([]);
    const [loadingOlder, setLoadingOlder] = useState(false);
    const current = useRef<LiveCheckResponseDto | undefined>(undefined);
    const lastOrdinal = useRef(0);
    const reading = useRef(false);
    const mounted = useRef(true);

    /** Строки по возрастанию ordinal без повторов, не больше keep последних. */
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
                // В памяти остаются только keep последних: пропущенное раньше читает olderJournal.
                lastOrdinal.current = Math.max(lastOrdinal.current, current.current.journal.lastOrdinal - keep);
                const page = await checksApi.listJournal({ projectId, checkId: snapshot.id, after: lastOrdinal.current, limit: Math.min(journalPage, Math.max(keep, 1)) });
                if (!mounted.current || current.current?.id !== snapshot.id) return;
                if (!page.items.length) {
                    // Кольцо экрана вытеснило запрошенное: продолжаем с первой хранимой записи.
                    lastOrdinal.current = Math.max(lastOrdinal.current, page.journal.firstOrdinal - 1);
                    if (page.journal.lastOrdinal <= lastOrdinal.current) break;
                    continue;
                }
                merge(page.items);
            }
        } catch {
            // Следующее событие проверки повторит дочитывание.
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

    /** Показать проверку: ответ start* или снимок get (recentFrames — последняя страница журнала). */
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
                // Прежней проверки нет — режим начинается с пустой формы.
            }
        })();
        return () => {
            mounted.current = false;
            unsubscribe();
            const running = current.current;
            if (stopOnLeave && running && isLiveCheckActive(running.state)) void checksApi.stop({ projectId, checkId: running.id }).catch(() => undefined);
        };
    }, [projectId, mode, keep, stopOnLeave, apply, attach]);

    /** «Остановить». */
    const stop = useCallback(async () => {
        const running = current.current;
        if (!running) return;
        apply(await checksApi.stop({ projectId, checkId: running.id }));
    }, [projectId, apply]);

    /** Более ранние строки журнала перед первой загруженной. */
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
