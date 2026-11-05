import * as React from "react"
import { useTranslation } from "react-i18next"
import { cn } from "@/lib/utils"
import { XIcon } from "lucide-react"
import { Dialog as DialogPrimitive } from "radix-ui"

import { Button } from "@/components/ui/button"

/** The overlay of every modal: the page dims by 55 % and blurs a little. */
const dialogOverlayClasses = "fixed inset-0 z-50 bg-black/55 backdrop-blur-xs"

/**
 * A modal is raised glass at most the window height. One column that may shrink keeps truncated or nowrap children from widening it. A
 * content with a DialogBody (or a DialogForm around the body and the footer) becomes a column: the header and the footer stay, only the
 * body scrolls.
 */
const dialogContentClasses = "glass-raised fixed top-[50%] left-[50%] z-50 grid max-h-[calc(100dvh-3rem)] w-full max-w-[calc(100%-2rem)] translate-x-[-50%] translate-y-[-50%] grid-cols-[minmax(0,1fr)] gap-4 overflow-y-auto rounded-xl p-6 text-text-1 outline-none has-[>[data-slot=dialog-body]]:flex has-[>[data-slot=dialog-body]]:flex-col has-[>[data-slot=dialog-body]]:overflow-hidden has-[>[data-slot=dialog-form]]:flex has-[>[data-slot=dialog-form]]:flex-col has-[>[data-slot=dialog-form]]:overflow-hidden"

function Dialog({
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Root>) {
    return <DialogPrimitive.Root data-slot="dialog" {...props} />
}

function DialogTrigger({
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Trigger>) {
    return <DialogPrimitive.Trigger data-slot="dialog-trigger" {...props} />
}

function DialogPortal({
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Portal>) {
    return <DialogPrimitive.Portal data-slot="dialog-portal" {...props} />
}

function DialogClose({
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Close>) {
    return <DialogPrimitive.Close data-slot="dialog-close" {...props} />
}

function DialogOverlay({
    className,
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Overlay>) {
    return (
        <DialogPrimitive.Overlay
            data-slot="dialog-overlay"
            className={cn(
                dialogOverlayClasses,
                className
            )}
            {...props}
        />
    )
}

function DialogContent({
    className,
    children,
    showCloseButton = true,
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Content> & {
    showCloseButton?: boolean
}) {
    return (
        <DialogPortal data-slot="dialog-portal">
            <DialogOverlay />
            <DialogPrimitive.Content
                data-slot="dialog-content"
                className={cn(
                    dialogContentClasses,
                    "sm:max-w-lg",
                    className
                )}
                {...props}
            >
                {children}
                {showCloseButton && (
                    <DialogPrimitive.Close
                        data-slot="dialog-close"
                        className="absolute top-4 right-4 grid size-7 place-items-center rounded-md text-text-2 transition-colors duration-120 ease-glass outline-none hover:bg-glass-fill hover:text-text-1 focus-visible:ring-[3px] focus-visible:ring-focus/30 disabled:pointer-events-none [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4"
                    >
                        <XIcon />
                        <span className="sr-only"><CloseText /></span>
                    </DialogPrimitive.Close>
                )}
            </DialogPrimitive.Content>
        </DialogPortal>
    )
}

function DialogHeader({ className, ...props }: React.ComponentProps<"div">) {
    return (
        <div
            data-slot="dialog-header"
            className={cn("flex shrink-0 flex-col gap-2 text-center sm:text-left", className)}
            {...props}
        />
    )
}

/** The only scrolling region of a dialog; the header and the footer around it stay in place. */
function DialogBody({ className, ...props }: React.ComponentProps<"div">) {
    return (
        <div
            data-slot="dialog-body"
            className={cn("-mx-6 min-h-0 flex-1 overflow-y-auto overscroll-contain px-6", className)}
            {...props}
        />
    )
}

/** A form that holds the DialogBody and the DialogFooter, so Enter in a field and the submit button of the footer send it. */
function DialogForm({ className, ...props }: React.ComponentProps<"form">) {
    return (
        <form
            data-slot="dialog-form"
            className={cn("flex min-h-0 flex-1 flex-col gap-4", className)}
            {...props}
        />
    )
}

function DialogFooter({
    className,
    showCloseButton = false,
    children,
    ...props
}: React.ComponentProps<"div"> & {
    showCloseButton?: boolean
}) {
    return (
        <div
            data-slot="dialog-footer"
            className={cn(
                "flex shrink-0 flex-col-reverse gap-2 sm:flex-row sm:justify-end",
                className
            )}
            {...props}
        >
            {children}
            {showCloseButton && (
                <DialogPrimitive.Close asChild>
                    <Button variant="outline"><CloseText /></Button>
                </DialogPrimitive.Close>
            )}
        </div>
    )
}

function DialogTitle({
    className,
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Title>) {
    return (
        <DialogPrimitive.Title
            data-slot="dialog-title"
            className={cn("text-lg leading-none font-semibold tracking-[-0.02em]", className)}
            {...props}
        />
    )
}

function DialogDescription({
    className,
    ...props
}: React.ComponentProps<typeof DialogPrimitive.Description>) {
    return (
        <DialogPrimitive.Description
            data-slot="dialog-description"
            className={cn("text-sm text-muted-foreground", className)}
            {...props}
        />
    )
}

export {
    Dialog,
    DialogBody,
    DialogClose,
    DialogContent,
    DialogDescription,
    DialogFooter,
    DialogForm,
    DialogHeader,
    DialogOverlay,
    DialogPortal,
    DialogTitle,
    DialogTrigger,
    dialogContentClasses,
    dialogOverlayClasses,
}

function CloseText() {
    const { t } = useTranslation()
    return t("dialog.close")
}
