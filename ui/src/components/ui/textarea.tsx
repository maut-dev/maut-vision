import * as React from "react"
import { cn } from "@/lib/utils"
import { fieldClasses } from "@/components/ui/input"

function Textarea({ className, ...props }: React.ComponentProps<"textarea">) {
    return (
        <textarea
            data-slot="textarea"
            className={cn(
                "flex field-sizing-content min-h-16 w-full px-3 py-2 leading-normal",
                fieldClasses,
                className
            )}
            {...props}
        />
    )
}

export { Textarea }
