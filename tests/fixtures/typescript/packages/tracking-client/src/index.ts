export async function fetchTracking(cargoId: string): Promise<string> {
  return `/api/tracking/${cargoId}`;
}
