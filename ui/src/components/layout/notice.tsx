import type { ComponentProps, ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { VariantProps } from "class-variance-authority";
import { Alert, AlertDescription, AlertTitle, type alertVariants } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";

interface NoticeProps extends Omit<ComponentProps<"div">, "title"> {
    variant?: VariantProps<typeof alertVariants>["variant"];
    title?: ReactNode;
    /** A button that resolves the notice, for example "Load again". */
    action?: { label: string; onClick: () => void; disabled?: boolean; testId?: string };
    /** Shows "Hide": the owner of the notice state removes it. */
    onDismiss?: () => void;
}

/** A message in a section with an optional action and "Hide"; an error is announced at once, anything else politely. */
export function Notice({ variant, title, action, onDismiss, children, role, ...props }: NoticeProps) {
    const { t } = useTranslation();
    return (
        <Alert variant={variant} role={role ?? (variant === "destructive" ? "alert" : "status")} {...props}>
            {title !== undefined && <AlertTitle>{title}</AlertTitle>}
            <AlertDescription>
                <div>{children}</div>
                {(action || onDismiss) && (
                    <div className="mt-2 flex flex-wrap gap-2">
                        {action && <Button type="button" variant="outline" size="sm" disabled={action.disabled} data-testid={action.testId} onClick={action.onClick}>{action.label}</Button>}
                        {onDismiss && <Button type="button" variant="ghost" size="sm" onClick={onDismiss}>{t("common.hide")}</Button>}
                    </div>
                )}
            </AlertDescription>
        </Alert>
    );
}
