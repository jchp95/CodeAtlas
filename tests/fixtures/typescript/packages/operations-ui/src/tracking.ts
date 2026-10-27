import { fetchTracking } from "@fixture/tracking-client";

export async function loadTracking(cargoId: string) {
  return fetchTracking(cargoId);
}
