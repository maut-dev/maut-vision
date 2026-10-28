import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import {
    AlertDialog,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { subscribeCloseRequested } from "@/api/events/subscriptions";
import { systemApi } from "@/api/system/system-api";
import { Button } from "@/components/ui/button";
import { errorMessage } from "@/lib/format";

/** Экран с несохранёнными изменениями: признак, сохранение и явный отказ от изменений. */
interface UnsavedRegistration {
    dirty: boolean;
    save: () => Promise<void>;
    discard: () => void;
}

interface UnsavedChangesContext {
    register: (registration: { current: UnsavedRegistration } | null) => void;
    guard: (intent: () => void) => void;
}

const Context = createContext<UnsavedChangesContext>({ register: () => {}, guard: intent => intent() });

/**
 * Одна защита несохранённых изменений для всех переходов: внутри раздела, смена раздела или проекта, перезагрузка страницы
 * (beforeunload) и закрытие окна приложения (app.closeRequested от host: окно закрывается, только если изменений нет,
 * иначе после сохранения или отказа UI просит host закрыть его — system.close). Переход выполняется только после
 * успешного сохранения или явного отказа; ошибка сохранения остаётся в диалоге.
 */
export function UnsavedChangesProvider({ children }: { children: ReactNode }) {
    const { t } = useTranslation();
    const registration = useRef<{ current: UnsavedRegistration } | null>(null);
    const [pending, setPending] = useState<{ intent: () => void }>();
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();

    useEffect(() => {
        const beforeUnload = (event: BeforeUnloadEvent) => {
            if (!registration.current?.current.dirty) return;
            event.preventDefault();
            event.returnValue = "";
        };
        window.addEventListener("beforeunload", beforeUnload);
        return () => window.removeEventListener("beforeunload", beforeUnload);
    }, []);

    useEffect(() => subscribeCloseRequested(event => {
        const dirty = !!registration.current?.current.dirty;
        // The answer must reach the host within a second; the dialog opens alongside.
        systemApi.closeDecision({ requestId: event.data.requestId, allow: !dirty }).catch(() => {});
        if (!dirty) return;
        setError(undefined);
        setPending({ intent: () => { systemApi.close().catch(() => {}); } });
    }), []);

    const value = useMemo<UnsavedChangesContext>(() => ({
        register: next => { registration.current = next; },
        guard: intent => {
            if (registration.current?.current.dirty) {
                setError(undefined);
                setPending({ intent });
            } else intent();
        },
    }), []);

    function leave() {
        const intent = pending?.intent;
        setPending(undefined);
        registration.current?.current.discard();
        intent?.();
    }

    async function saveAndContinue() {
        const target = registration.current?.current;
        const intent = pending?.intent;
        if (!target || !intent) return;
        setBusy(true);
        setError(undefined);
        try {
            await target.save();
            setPending(undefined);
            intent();
        } catch (cause) {
            setError(errorMessage(cause, t("unsaved.saveFailed")));
        } finally {
            setBusy(false);
        }
    }

    return (
        <Context.Provider value={value}>
            {children}
            <AlertDialog open={!!pending} onOpenChange={open => { if (!open && !busy) setPending(undefined); }}>
                <AlertDialogContent data-testid="unsaved-guard" onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}>
                    <AlertDialogHeader>
                        <AlertDialogTitle>{t("unsaved.title")}</AlertDialogTitle>
                        <AlertDialogDescription>
                            {t("unsaved.text")}
                        </AlertDialogDescription>
                    </AlertDialogHeader>
                    <div className="min-h-6" aria-live="polite">
                        {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
                    </div>
                    <AlertDialogFooter className="sm:flex-wrap">
                        <AlertDialogCancel disabled={busy}>{t("unsaved.continue")}</AlertDialogCancel>
                        <Button variant="outline" disabled={busy} data-testid="unsaved-leave" onClick={leave}>{t("unsaved.leave")}</Button>
                        <Button disabled={busy} data-testid="unsaved-save" onClick={() => void saveAndContinue()}>
                            {busy ? t("common.saving") : t("unsaved.save")}
                        </Button>
                    </AlertDialogFooter>
                </AlertDialogContent>
            </AlertDialog>
        </Context.Provider>
    );
}

/** Переход, который при несохранённых изменениях сначала спрашивает пользователя. */
export function useNavigationGuard(): (intent: () => void) => void {
    return useContext(Context).guard;
}

/** Регистрирует экран с несохранёнными изменениями на время его жизни. */
export function useUnsavedChanges(dirty: boolean, save: () => Promise<void>, discard: () => void) {
    const { register } = useContext(Context);
    const latest = useRef<UnsavedRegistration>({ dirty, save, discard });
    latest.current = { dirty, save, discard };
    useEffect(() => {
        register(latest);
        return () => register(null);
    }, [register]);
}
