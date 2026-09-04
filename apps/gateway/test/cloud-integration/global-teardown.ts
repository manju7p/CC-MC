import { stopLiveApi } from "./support/live-cloud-fixture";

/** Jest `globalTeardown` for the cloud-integration suite - stops the spawned API process. */
module.exports = async function globalTeardown(): Promise<void> {
  stopLiveApi();
};
