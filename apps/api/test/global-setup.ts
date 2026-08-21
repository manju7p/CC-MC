/**
 * Jest `globalSetup` - runs once before the whole e2e suite. Resets the
 * test database schema and seeds it, so every test file starts from the
 * same known state. Uses `synchronize` here (test DB only) rather than the
 * generated migration, which is a deliberate pragmatic shortcut for the
 * test database - the dev/prod database is always migrated via the real
 * migration in src/migrations, never synchronize (see app.module.ts).
 */
import * as path from "path";
import * as dotenv from "dotenv";

dotenv.config({ path: path.resolve(__dirname, "../.env.test") });

import { DataSource } from "typeorm";
import { ALL_ENTITIES } from "../src/entities";
import { seed } from "../src/seed";

module.exports = async function globalSetup() {
  const dataSource = new DataSource({
    type: "postgres",
    url: process.env.DATABASE_URL,
    entities: ALL_ENTITIES,
    synchronize: false,
  });
  await dataSource.initialize();
  await dataSource.synchronize(true); // drop + recreate schema for a clean, deterministic test run
  await seed(dataSource);
  await dataSource.destroy();
};
