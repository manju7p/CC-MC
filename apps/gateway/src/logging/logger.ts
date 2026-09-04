/**
 * Minimal structured JSON-line logger. No external dependency - the gateway
 * is a small, boring process (Rule 11) and a full logging framework
 * (pino/winston) isn't justified yet. Each call writes exactly one JSON
 * object per line to stdout, which:
 *
 *  - is trivially machine-parseable for future log shipping,
 *  - is captured by WinSW's own stdout redirection into its rolling log
 *    files (docs/gateway-decision.md §7) with zero extra wiring, and
 *  - stays readable directly in a terminal during local development.
 *
 * This is distinct from WinSW's wrapper-level logs: this logger captures
 * the *application's* view of its own state (gateway ID, centre ID,
 * component, structured fields); WinSW's logs capture the raw process
 * stdout/stderr including failures the app itself can't log (e.g. it
 * failing to start at all). Both layers are intentional - see
 * docs/gateway-decision.md §7.
 */
export type LogLevel = "debug" | "info" | "warn" | "error";

export interface LogFields {
  [key: string]: unknown;
}

export class Logger {
  constructor(private readonly component: string) {}

  private write(level: LogLevel, message: string, fields?: LogFields): void {
    const line = {
      timestamp: new Date().toISOString(),
      level,
      component: this.component,
      message,
      ...fields,
    };
    // stdout, not stderr, even for warn/error: WinSW's config (Checkpoint 2)
    // captures both streams into separate rolling files, and keeping all
    // structured application logs on one stream keeps their relative
    // ordering intact when read back as a single log.
    process.stdout.write(JSON.stringify(line) + "\n");
  }

  debug(message: string, fields?: LogFields): void {
    this.write("debug", message, fields);
  }

  info(message: string, fields?: LogFields): void {
    this.write("info", message, fields);
  }

  warn(message: string, fields?: LogFields): void {
    this.write("warn", message, fields);
  }

  error(message: string, fields?: LogFields): void {
    this.write("error", message, fields);
  }

  /** Returns a new Logger scoped to a sub-component, e.g. "gateway.device-manager". */
  child(subComponent: string): Logger {
    return new Logger(`${this.component}.${subComponent}`);
  }
}
