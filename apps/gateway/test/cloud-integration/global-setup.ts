import { resetAndSeedDatabase, startLiveApi } from "./support/live-cloud-fixture";

/**
 * Jest `globalSetup` for the cloud-integration suite - runs once, in the
 * main Jest process, before any test file in this directory. Resets and
 * seeds ccmc_gateway_test, then starts the real compiled API against it.
 * See support/live-cloud-fixture.ts for the full design.
 */
module.exports = async function globalSetup(): Promise<void> {
  resetAndSeedDatabase();
  await startLiveApi();
};
