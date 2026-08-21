/**
 * Jest `setupFiles` entry - runs inside each test file's process before the
 * test framework and any application code loads, so it's the reliable
 * place to point DATABASE_URL/JWT_SECRET at the *test* database rather
 * than dev.
 */
import * as path from "path";
import * as dotenv from "dotenv";

dotenv.config({ path: path.resolve(__dirname, "../.env.test") });
