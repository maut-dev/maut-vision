import { useCallback, useEffect, useRef, useState } from "react";
import type { TrainingEpochDto, TrainingLogEntryDto, TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { isActiveTrainingRun } from "@/api/projects/training/training-parameters";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import "./training/locale";

/** Messages in the window "Latest messages" and on a page of "Full history". */
export const trainingLogWindow = 200;
const epochPage = 1000;

export type TrainingLogMode = "live" | "history";

/**
 * The epoch history and the log of the chosen run without polling. Epochs are read by pages in full, then read on with after when
 * epochCount of the run snapshot (training.changed) is larger than loaded; after the run result the history is read again in full — Core
 * replaces it with history.jsonl. The log: "Latest messages" — tailLog, then listLog with after as logCount grows (a window of the last
 * 200); "Full history" — pages of listLog by 200 with "Next log page".
 */
export function useTrainingRunDetails(projectId: string, run: TrainingRunResponseDto | undefined) {
    const runId = run?.id;
    const [epochs, setEpochs] = useState<readonly TrainingEpochDto[]>([]);
    const [epochsLoaded, setEpochsLoaded] = useState(false);
    const [log, setLog] = useState<readonly TrainingLogEntryDto[]>([]);
    const [logTotal, setLogTotal] = useState(0);
    const [logMode, setLogMode] = useState<TrainingLogMode>("live");
    const [historyCursor, setHistoryCursor] = useState<number | undefined>();
    const [historyNext, setHistoryNext] = useState<number | null>(null);
    const [error, setError] = useState<string>();
    const epochCursor = useRef<number | undefined>(undefined);
    const logCursor = useRef<number | null>(null);
    const epochQueue = useRef<{ busy: boolean; again: boolean; reset: boolean }>({ busy: false, again: false, reset: false });
    const logQueue = useRef({ busy: false, again: false });
    const generation = useRef(0);
    const logGeneration = useRef(0);

    const loadEpochs = useCallback(async (reset: boolean) => {
        if (!runId) return;
        const queue = epochQueue.current;
        if (queue.busy) {
            queue.again = true;
            queue.reset ||= reset;
            return;
        }
        queue.busy = true;
        const own = generation.current;
        try {
            let after = reset ? undefined : epochCursor.current;
            const items: TrainingEpochDto[] = [];
            for (;;) {
                const page = await trainingApi.listEpochs({ projectId, runId, after, limit: epochPage });
                if (own !== generation.current) return;
                items.push(...page.items);
                if (page.items.length) after = page.items[page.items.length - 1].epoch;
                if (page.nextAfter === null) break;
                after = page.nextAfter;
            }
            epochCursor.current = after;
            setEpochs(current => {
                const base = reset ? [] : current;
                const seen = new Set(base.map(item => item.epoch));
                return [...base, ...items.filter(item => !seen.has(item.epoch))];
            });
            setEpochsLoaded(true);
        } catch (cause) {
            if (own === generation.current) setError(errorMessage(cause, i18n.t("training:loadErrors.epochs")));
        } finally {
            if (own === generation.current) {
                queue.busy = false;
                if (queue.again) {
                    const again = queue.reset;
                    queue.again = false;
                    queue.reset = false;
                    void loadEpochs(again);
                }
            }
        }
    }, [projectId, runId]);

    /** "Latest messages": after the tail only new entries are read. */
    const loadLiveLog = useCallback(async () => {
        if (!runId) return;
        const queue = logQueue.current;
        if (queue.busy) {
            queue.again = true;
            return;
        }
        queue.busy = true;
        const own = logGeneration.current;
        try {
            if (logCursor.current === null) {
                const tail = await trainingApi.tailLog({ projectId, runId, limit: trainingLogWindow });
                if (own !== logGeneration.current) return;
                logCursor.current = tail.nextAfter ?? 0;
                setLog(tail.items);
                setLogTotal(tail.total);
            } else {
                for (;;) {
                    const page = await trainingApi.listLog({ projectId, runId, after: logCursor.current, limit: trainingLogWindow });
                    if (own !== logGeneration.current) return;
                    if (page.items.length) logCursor.current = page.items[page.items.length - 1].sequence;
                    setLog(current => [...current, ...page.items].slice(-trainingLogWindow));
                    setLogTotal(page.total);
                    if (page.nextAfter === null || !page.items.length) break;
                }
            }
        } catch (cause) {
            if (own === logGeneration.current) setError(errorMessage(cause, i18n.t("training:loadErrors.log")));
        } finally {
            if (own === logGeneration.current) {
                queue.busy = false;
                if (queue.again) {
                    queue.again = false;
                    void loadLiveLog();
                }
            }
        }
    }, [projectId, runId]);

    // A new run — everything from the start.
    useEffect(() => {
        generation.current++;
        logGeneration.current++;
        epochCursor.current = undefined;
        logCursor.current = null;
        epochQueue.current = { busy: false, again: false, reset: false };
        logQueue.current = { busy: false, again: false };
        setEpochs([]);
        setEpochsLoaded(false);
        setLog([]);
        setLogTotal(0);
        setLogMode("live");
        setHistoryCursor(undefined);
        setError(undefined);
    }, [runId]);

    const epochCount = run?.epochCount ?? 0;
    const finished = !!run && !isActiveTrainingRun(run.state);
    const loadedEpochs = epochs.length;
    useEffect(() => {
        if (!runId) return;
        if (!epochsLoaded) void loadEpochs(true);
        else if (epochCount > loadedEpochs) void loadEpochs(false);
    }, [runId, epochCount, epochsLoaded, loadedEpochs, loadEpochs]);

    // The run result replaces the epoch history with the worker file: read it again in full once.
    const replaced = useRef<string | undefined>(undefined);
    useEffect(() => {
        if (!runId || !finished || !epochsLoaded || replaced.current === runId) return;
        replaced.current = runId;
        void loadEpochs(true);
    }, [runId, finished, epochsLoaded, loadEpochs]);

    const logCount = run?.logCount ?? 0;
    useEffect(() => {
        if (runId && logMode === "live") void loadLiveLog();
    }, [runId, logCount, logMode, loadLiveLog]);

    // "Full history": a page from the cursor; a cursor change is the next page.
    useEffect(() => {
        if (!runId || logMode !== "history") return;
        const own = logGeneration.current;
        let valid = true;
        void trainingApi.listLog({ projectId, runId, after: historyCursor, limit: trainingLogWindow })
            .then(page => {
                if (!valid || own !== logGeneration.current) return;
                setLog(page.items);
                setLogTotal(page.total);
                setHistoryNext(page.nextAfter);
            })
            .catch(cause => { if (valid) setError(errorMessage(cause, i18n.t("training:loadErrors.log"))); });
        return () => { valid = false; };
    }, [projectId, runId, logMode, historyCursor]);

    const showLog = useCallback((mode: TrainingLogMode) => {
        if (mode === logMode) return;
        logGeneration.current++;
        logCursor.current = null;
        logQueue.current = { busy: false, again: false };
        setHistoryCursor(undefined);
        setHistoryNext(null);
        setLog([]);
        setLogMode(mode);
    }, [logMode]);

    return {
        epochs, epochsLoaded, log, logTotal, logMode, historyNext, error,
        showLog,
        nextLogPage: () => { if (historyNext !== null) setHistoryCursor(historyNext); },
    };
}
