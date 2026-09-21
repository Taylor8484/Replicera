# Third-Party Notices

Replicera source code is licensed under Apache-2.0. Third-party packages retain their own licenses; the project license does not replace those terms.

Runtime dependencies include:

- `Microsoft.PowerPlatform.Dataverse.Client` under the [Microsoft Power Apps and Dynamics 365 SDK license](https://www.microsoft.com/en-us/business-applications/legal/slt-dynamics365-sdk/). Its distributable DLLs are subject to the distribution requirements in that license.
- `Microsoft.Data.SqlClient` and its supporting Microsoft packages under the MIT license, except any files that carry their own included notice.
- `Npgsql` under the PostgreSQL license.
- `Oracle.ManagedDataAccess.Core` under the [Oracle Free Use Terms and Conditions](https://www.oracle.com/downloads/licenses/oracle-free-license.html).

Test dependencies include xUnit under Apache-2.0 and Microsoft test/coverage packages under MIT. Exact resolved versions and transitive dependencies are recorded in `packages.lock.json` files. Release packaging must preserve notices shipped with dependencies and re-check licenses when versions change.
