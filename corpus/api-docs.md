# Brightwell Appointments API

The Appointments API lets partner applications search clinician availability, book appointments, and receive updates when appointments change. This API belongs to the fictional company Brightwell Health and exists only to demonstrate how code-heavy documentation is chunked.

The current version is v2. Version 1 was retired and now returns HTTP 410 Gone for every request.

## Getting Started

### Base URL

All requests are made over HTTPS to the following base URL. Plain HTTP requests are rejected rather than redirected, so that credentials are never sent unencrypted by mistake.

```
https://api.brightwell.example/appointments/v2
```

### Authentication

The API uses the OAuth 2.0 client credentials flow. Exchange your client ID and secret for an access token at the token endpoint, then send the token in the `Authorization` header of every request. Tokens expire after 3600 seconds; request a new one shortly before expiry rather than waiting for a 401 response.

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;

var http = new HttpClient();
var tokenResponse = await http.PostAsync(
    "https://auth.brightwell.example/oauth2/token",
    new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = clientId,
        ["client_secret"] = clientSecret,
        ["scope"] = "appointments.read appointments.write"
    }));

var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>();
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", token!.AccessToken);
```

Request only the scopes your integration needs. Read-only integrations should ask for `appointments.read` alone, which allows the platform team to grant access more quickly during partner review.

### Rate Limits

Each client may make up to 600 requests per minute. When you exceed the limit, the API returns HTTP 429 Too Many Requests with a `Retry-After` header indicating how many seconds to wait. Clients that repeatedly ignore the header are temporarily suspended for 15 minutes.

## Endpoints

### Search Availability

`GET /slots` returns open appointment slots for a clinic within a date range. The range may not exceed 14 days, and results are sorted by start time.

| Query parameter | Type   | Required | Description                                 |
|-----------------|--------|----------|---------------------------------------------|
| clinicId        | string | Yes      | Identifier of the clinic                    |
| from            | date   | Yes      | First day to search (ISO 8601)              |
| to              | date   | Yes      | Last day to search, at most 14 days later   |
| specialty       | string | No       | Filter by specialty, for example `dermatology` |

```json
{
  "clinicId": "clinic-204",
  "slots": [
    {
      "slotId": "slot-88213",
      "clinicianId": "dr-okafor",
      "start": "2025-03-04T09:30:00Z",
      "durationMinutes": 20,
      "specialty": "general-practice"
    }
  ],
  "nextPageToken": null
}
```

### Create an Appointment

`POST /appointments` books a slot for a patient. The slot is held for the patient as soon as the request is accepted, and the response contains the new appointment's identifier and status.

Every create request must include an `Idempotency-Key` header containing a unique value such as a UUID. If a network failure means you are unsure whether the booking succeeded, resend the same request with the same key: the API will return the original appointment instead of creating a duplicate. Keys are remembered for 24 hours.

```csharp
public sealed record CreateAppointmentRequest(
    string SlotId,
    string PatientId,
    string Reason,
    bool SendReminder = true);

var request = new HttpRequestMessage(HttpMethod.Post, "appointments")
{
    Content = JsonContent.Create(new CreateAppointmentRequest(
        SlotId: "slot-88213",
        PatientId: "pat-55120",
        Reason: "Annual check-up"))
};
request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

var response = await http.SendAsync(request);
response.EnsureSuccessStatusCode();
```

A successful request returns HTTP 201 Created:

```json
{
  "appointmentId": "appt-30017",
  "status": "confirmed",
  "slotId": "slot-88213",
  "patientId": "pat-55120",
  "reminderScheduledFor": "2025-03-03T09:30:00Z"
}
```

### Cancel an Appointment

`DELETE /appointments/{appointmentId}` cancels a booking and releases the slot. Cancellations made less than 24 hours before the start time are still accepted, but the appointment is marked as `late-cancelled` so that clinics can report on short-notice changes. Cancelling an appointment that has already started returns HTTP 409 Conflict.

## Webhooks

Instead of polling for changes, you can register a webhook URL to receive events when appointments are created, rescheduled or cancelled. Events are delivered as HTTP POST requests with a JSON body.

Each delivery includes an `X-Brightwell-Signature` header containing an HMAC-SHA256 signature of the raw request body, computed with your webhook secret. Always verify the signature before trusting the payload, and compare signatures using a constant-time comparison to avoid timing attacks.

```csharp
using System.Security.Cryptography;
using System.Text;

static bool IsValidSignature(string rawBody, string signatureHeader, string secret)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody));
    var provided = Convert.FromHexString(signatureHeader);
    return CryptographicOperations.FixedTimeEquals(expected, provided);
}
```

If your endpoint does not respond with a 2xx status within 10 seconds, the delivery is retried with exponential backoff for up to 72 hours. After that, the event is dropped and appears in the failed-deliveries report in the partner dashboard.

## Errors

Errors use the RFC 7807 problem details format. The `type` field is a stable identifier you can branch on in code, while `detail` is a human-readable message that may change between releases.

```json
{
  "type": "https://api.brightwell.example/problems/slot-unavailable",
  "title": "Slot unavailable",
  "status": 409,
  "detail": "Slot slot-88213 was booked by another request.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00"
}
```

| Status | Meaning                                     | Should you retry?         |
|--------|---------------------------------------------|---------------------------|
| 400    | The request body or parameters are invalid  | No, fix the request       |
| 401    | The access token is missing or expired      | Yes, after refreshing the token |
| 409    | The slot was taken or the state changed     | No, search again          |
| 429    | Rate limit exceeded                         | Yes, after `Retry-After`  |
| 503    | Temporary outage                            | Yes, with backoff         |

Include the `traceId` value when contacting partner support, as it lets the support team find the exact request in the logs.
