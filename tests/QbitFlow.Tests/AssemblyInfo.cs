// The integration tests each spin up a WebApplicationFactory / EF-migrated SQLite database;
// running them in parallel races on startup. The whole suite still runs in a couple of seconds.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
