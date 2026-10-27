const trackingApiBaseUrl = "https://zas-track-api:7182/api";
export async function getTracking() {
  const response = await fetch(`${trackingApiBaseUrl}/tracking`);
  return response.json();
}
