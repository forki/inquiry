v1.0

Requirements for taking the step to 1.0

* Running inQuiry in at least 10 different production scenarios
* Have a working contributing open source project including rules of conduct, and having recieved and merged at least 5 pull requests
* Support for multiversion inRiver
* Support for .NET Core


v0.5

Setup a CI pipeline. Use travic-ci in order to build each new commit to VCS.

* Setup TravisCI
* Get the project to build with Mono
* Get the project to build with .NET Core
* Setup an Azure VM with inRiver for testing
* Make sure that tests run on Mono


v0.4

Create support to connect to two or more inRiver servers at the same time. When initializing
the type provider, the connection details must be specified.

```fsharp
type inRiver1 = inQuiry<"https://pim.dev:8080", "pimuser1", "pimuser1">
let product1 = inRiver1.Product("SKU123")

type inRiver2 = inQuiry<"https://pim.test:8080", "pimuser1", "pimuser1">
let product2 = inRiver2.Product("SKU123")
```

* Implement rest of the CRUD operations supported by inRiver Remoting.
* Make it possible to add a CVL option with something like `pim.MainCategory.add ("furniture", "Furniture")`

v0.3

The purpose of v0.3 is to query the inRiver API for several entities.

* Query API
* Opt-in logging for the code generation
* Add XML comments for all the fields
* Deal with relations between entities, handle links
* Implement FieldSets
* Implement Categories
* Implement Unique
* Implement Hidden
* Implement Exclude from Default View
* Create fsi files to protect internal members of the library
  - Not so sure this is possible as the generated code need to be accessing these internal members publicly

* Also check if the connection settings from <inRiver.Integration.Properties.Settings> are available
* Deal with MainPictureId

* Write example on
  - How to work with the XML field, query and update it
  - How to filter out specific fieldsets
  - How to filter out categories

BUGS
* BUG: Can't handle a CVL with the same name as Entity

META
* Create a logo for the nuget package