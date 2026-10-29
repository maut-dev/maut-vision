/** The UI showed the library after the start; the host writes the startup time to the log once. */
export interface ReadyRequestDto {
    /** The number of received projects, at least 0. */
    projects: number;
}
