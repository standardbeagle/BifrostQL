/**
 * Example: Using BifrostBinaryClient to query a BifrostQL WebSocket endpoint.
 *
 * Prerequisites:
 *   - A BifrostQL server running with UseBifrostBinary() configured
 *   - WebSocket endpoint available at the specified URL
 *
 * Run:
 *   npx tsx example.ts
 */
import { BifrostBinaryClient } from "./src/index.js";

async function main() {
  const client = new BifrostBinaryClient({
    url: "ws://localhost:5000/bifrost-ws",
    requestTimeoutMs: 10_000,
    onOpen: () => console.log("Connected"),
    onClose: (code, reason) => console.log(`Disconnected: ${code} ${reason}`),
    onError: (err) => console.error("Error:", err),
  });

  await client.connect();

  // Run multiple queries concurrently over the same connection
  const [users, orders] = await Promise.all([
    client.query("{ users { id name email } }"),
    client.query("{ orders { id total status } }"),
  ]);

  console.log("Users:", JSON.stringify(users.data, null, 2));
  console.log("Orders:", JSON.stringify(orders.data, null, 2));

  // Mutation with variables
  const result = await client.mutate(
    `mutation ($input: usersInput!) {
      insert_users(input: $input) { id name }
    }`,
    { input: { name: "Alice", email: "alice@example.com" } }
  );

  if (result.errors.length > 0) {
    console.error("Mutation errors:", result.errors);
  } else {
    console.log("Inserted:", result.data);
  }

  client.close();
}

main().catch(console.error);
