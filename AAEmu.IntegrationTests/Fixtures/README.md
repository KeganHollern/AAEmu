# Game MySQL tests

The `GameMySql` collection uses Testcontainers by default. CI keeps this path.
The fixture loads the Game schema into a disposable MySQL 8 database.

For local tests without Docker, set `AAEMU_GAME_TEST_MYSQL_CONNECTION` to a
connection string for the current loopback test server. The string must use a
loopback IP address, TCP, and no database name. The fixture rejects other hosts
and every supplied database name, including names that contain `test`.

The fixture creates `aaemu_game_test_<random-guid>` and uses only that schema.
It removes the same schema after the tests. A schema load failure also removes
the generated schema. The test account needs schema creation and deletion for
these generated names, plus table, routine, and trigger access inside them.
Do not use this option with a production server or a forwarded production port.
Do not put connection strings with private credentials in Git or test logs.

The cluster workspace provides the local MySQL 8.0.36 server through
`scripts/dev-mysql.sh start`. Its loopback port is `33306`.
The local fixture uses no new Kubernetes access, Pod, Job, or workflow.

Run the tests with the selected local connection:

```sh
dotnet test --project AAEmu.IntegrationTests --configuration Release --no-build -- --filter-trait Category=GameMySql
```
