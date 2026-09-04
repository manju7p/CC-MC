import { createTestStorage } from "../support/test-storage";

describe("SqliteLocalStorage - schema/migrations", () => {
  it("creates the expected tables on first init()", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    // Reach into the underlying connection only for this structural check -
    // everything else in this suite goes through the public LocalStorage
    // interface, per the "test the interface, not internals" default.
    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    const tables = db
      .prepare("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")
      .all() as { name: string }[];
    const tableNames = tables.map((t) => t.name);

    expect(tableNames).toEqual(
      expect.arrayContaining(["schema_migrations", "gateway_metadata", "local_transactions", "outbox_records"]),
    );

    await handle.storage.close();
    handle.cleanup();
  });

  it("records the applied migration in schema_migrations", async () => {
    const handle = createTestStorage();
    await handle.storage.init();

    const db = (handle.storage as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    const rows = db.prepare("SELECT version, name FROM schema_migrations ORDER BY version").all() as {
      version: number;
      name: string;
    }[];

    expect(rows).toEqual([{ version: 1, name: "initial_schema" }]);

    await handle.storage.close();
    handle.cleanup();
  });

  it("is idempotent: calling init() again (fresh instance, same file) does not re-apply or error", async () => {
    const handle = createTestStorage();
    await handle.storage.init();
    await handle.storage.close();

    // Reopen against the SAME database file with a second, independent
    // SqliteLocalStorage instance - simulates a restart.
    const { SqliteLocalStorage } = await import("../../src/storage/sqlite-local-storage");
    const { Logger } = await import("../../src/logging/logger");
    const secondInstance = new SqliteLocalStorage(handle.config, new Logger("test"));
    await expect(secondInstance.init()).resolves.toBeUndefined();

    const db = (secondInstance as unknown as { db: import("node:sqlite").DatabaseSync }).db;
    const rows = db.prepare("SELECT version FROM schema_migrations").all();
    expect(rows).toHaveLength(1); // still exactly one migration record, not duplicated

    await secondInstance.close();
    handle.cleanup();
  });
});
