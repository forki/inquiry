﻿module inQuiry.TypeProvider

open System
open System.Reflection
open Microsoft.FSharp.Core.CompilerServices
open Microsoft.FSharp.Quotations
open ProviderImplementation
open ProviderImplementation.ProvidedTypes
open inQuiry
open inRiver.Remoting

// remove entity ID from field name
let fieldNamingConvention entityID (fieldName : string) =
    // try get configuration value if this naming convention is active
    let foundConfigValue, value = bool.TryParse(Configuration.ConfigurationManager.AppSettings.["inQuiry:fieldNamingConvention"])
    // apply fieldNamingConvention if configuration not found or unparsable, otherwise use configuration value
    if (not foundConfigValue) || value then
        fieldName.Replace(entityID, String.Empty)
    else
        fieldName

// make first letter lower case
// > camelCase "ProductName"
// -> "productName"
let toCamelCase = function
| s when s = null -> null
| s when s = String.Empty -> s
| s when s.Length = 1 -> s.ToLower()
| s -> System.Char.ToLower(s.[0]).ToString() + s.Substring(1)

// turn a value expression to an option value expression
let optionPropertyExpression<'a when 'a : equality> (valueExpression : Expr<'a>) =
    <@@
        let value = (%valueExpression : 'a)
        if value = Unchecked.defaultof<'a> then
            None
        else
            Some(value)
        @@>

let uncheckedExpression (valueExpression : Expr<'a>) =
    <@@ (% valueExpression : 'a ) @@>


// simplify the Objects.LocaleString to an immutable map
type LocaleString = Map<string, string>

// the available field data types
type DataType = | Boolean | CVL | DateTime | Double | File | Integer | LocaleString | String | Xml

type DataType with
    // Data type is represented as a string in inRiver
    static member parse = function
        | "Boolean" -> Boolean
        | "CVL" -> CVL
        | "DateTime" -> DateTime
        | "Double" -> Double
        | "File" -> File
        | "Integer" -> Integer
        | "LocaleString" -> LocaleString
        | "String" -> String
        | "Xml" -> Xml
        | dt -> failwith (sprintf "Unknown field data type: %s" dt)
    // Get the .NET Type for this data type
    static member toType = function
        | Boolean -> typeof<bool>
        | CVL -> typeof<obj>
        | DateTime -> typeof<System.DateTime>
        | Double -> typeof<double>
        | File -> typeof<obj>
        | Integer -> typeof<int>
        | LocaleString -> typeof<LocaleString>
        | String -> typeof<string>
        | Xml -> typeof<obj>
    // Get the Option type for data type
    static member toOptionType dataType =
        typedefof<Option<_>>.MakeGenericType([|dataType |> DataType.toType|])
    // Shortcut
    static member stringToType = DataType.parse >> DataType.toType

// this is the entity value that is returned from the type provider
type Entity (entityType, entity : Objects.Entity)  =

    // only called with entityType, create a new entity
    new(entityType) = Entity(entityType, Objects.Entity.CreateEntity(entityType))

    // called with entity, make sure we extract entityType
    new(entity : Objects.Entity) = Entity(entity.EntityType, entity)

    member this.PropertyValue fieldTypeId  = 
        match entity.GetField(fieldTypeId) with
        // expected a field, but it is not there
        | null -> failwith (sprintf "Field %s was not set on entity %s:%d" fieldTypeId entityType.Id entity.Id)
        | field -> field.Data


    member this.Entity = entity
    
    // default properties
    member this.ChangeSet = entity.ChangeSet

    member this.Completeness =
        if entity.Completeness.HasValue then
            Some entity.Completeness.Value
        else
            None

    member this.CreatedBy = entity.CreatedBy
    
    member this.DateCreated = entity.DateCreated

    member this.EntityType = entityType
    
    member this.FieldSetId = entity.FieldSetId
    
    member this.Id = entity.Id

    member this.LastModified = entity.LastModified

    member this.LoadLevel = entity.LoadLevel

    member this.Locked = entity.Locked

    member this.MainPictureId =
        if entity.MainPictureId.HasValue then
            Some entity.MainPictureId.Value
        else
            None
    
    member this.ModifiedBy = entity.ModifiedBy

    member this.Version = entity.Version
    
   
type EntityTypeFactory (entityType : Objects.EntityType)  =
   
    // filter all FieldTypes that are mandatory
    let mandatoryFieldTypes =
        entityType.FieldTypes
        // filter out only mandatory fields
        |> Seq.filter (fun fieldType -> fieldType.Mandatory)
        // make sure they're sorted by index
        |> Seq.sortBy (fun fieldType -> fieldType.Index)
        // change their orders so mandatory without default value comes first
        |> Seq.sortBy (fun fieldType -> if fieldType.DefaultValue = null then 1 else 2)
        |> Seq.toList

    let fieldTypeToProvidedParameter =
        // a constructor parameter name should remove the leading entityTypeID and make camel case
        let providedParameterNamingConvention fieldTypeID = 
            // the function that creates the identifier
            let providedParameterNamingConvention_ id = (fieldNamingConvention entityType.Id id) |> toCamelCase
            // the expected identifier
            let result = providedParameterNamingConvention_ fieldTypeID
            // another field will become the same parameter name
            let hasConflictingField = 
                entityType.FieldTypes 
                |> Seq.filter (fun ft -> fieldTypeID <> ft.Id)
                |> Seq.exists (fun ft -> result = (providedParameterNamingConvention_ ft.Id))
            // return
            if hasConflictingField then
                // there is a conflict, return the original, but to camel case
                fieldTypeID |> toCamelCase
            else
                result

        // map field type to provided parameter
        List.map (fun (fieldType : Objects.FieldType) ->
            // these fields are all mandatory
            if fieldType.DefaultValue = null then
                // so they are required as constructor parameters
                ProvidedParameter((providedParameterNamingConvention fieldType.Id), (fieldType.DataType |> DataType.stringToType))
            else
                // unless there is a default value, then the constructor parameter can be optional
                ProvidedParameter((providedParameterNamingConvention fieldType.Id), (fieldType.DataType |> DataType.stringToType), optionalValue = fieldType.DefaultValue))

    let mandatoryProvidedParameters =
        fieldTypeToProvidedParameter mandatoryFieldTypes

    // create an invoke expression for generated constructors
    let constructorExpression entityTypeID (fieldTypes : Objects.FieldType list) =
        fun (args : Expr list) ->
            // create the entity
            let emptyConstructorExpr =
                <@
                    // get the entity type
                    let entityType = 
                        match inRiverService.getEntityTypeById(entityTypeID) with
                        | Some result -> result
                        | None -> failwith (sprintf "Was expecting entity type %s, but couldn't find it in inRiver service" entityTypeID)

                    // create a new instance of the entity
                    Entity(Objects.Entity.CreateEntity(entityType))
                    @>
            let _constructorExpression =
                args
                |> List.zip fieldTypes
                |> List.fold (fun entityExpr (fieldType, argExpr) ->
                    let fieldTypeId = fieldType.Id
                    match DataType.parse fieldType.DataType with
                    | String ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : string)
                            entity
                            @>
                    | Integer ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : int)
                            entity
                            @>
                    | Boolean ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : bool)
                            entity
                            @>
                    | Double ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : double)
                            entity
                            @>
                    | DateTime ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : DateTime)
                            entity
                            @>
                    | LocaleString ->
                        <@
                            let entity = (% entityExpr : Entity)
                            entity.Entity.GetField(fieldTypeId).Data <- (%% argExpr : LocaleString)
                            entity
                            @>
                    // NOTE one does not simply implement CVL lists
                    | CVL | Xml | File -> 
                        <@ (% entityExpr : Entity) @>
                    ) emptyConstructorExpr
            <@@ %_constructorExpression @@>
    
    let createExpression entityTypeID =
        fun (args : Expr list) ->
        <@@
            let entity = (%% args.[0] : Objects.Entity)
            // is entity of the correct type?
            match entity.EntityType with
            | null -> failwith (sprintf "Unable to create strong type %s. EntityType was not set on entity." entityTypeID)
            | entityType when entityType.Id <> entityTypeID -> failwith (sprintf "Unable to create strong type %s. Entity source was %s." entityTypeID entity.EntityType.Id)
            | _ -> Entity(entity)                
            @@>

    // will save the entity to inRiver
    // Returns Result of the saved entity.
    // * Ok<TEntity>
    // * Error<Exception>
    let saveExpression =
        fun (args : Expr list) ->
        <@@
            let entity = (%% args.[0] : Entity)
            try
                // save to inRiver -> wrap result entity in TEntity instance
                Ok (Entity(inRiverService.save(entity.Entity)))
            with
                | ex -> Error ex
            @@>

    let stringValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%%(args.[0]) : Entity)
            // convert the value to string
            (entity.PropertyValue fieldTypeID) :?> string
            @>
            
    let localeStringValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%%(args.[0]) : Entity)
            // get the field value
            let localeString = (entity.PropertyValue fieldTypeID) :?> Objects.LocaleString
            // convert to immutable map
            match localeString with
            | null -> Map.empty
            | ls -> ls.Languages
                    |> Seq.map (fun lang -> (lang.Name, localeString.[lang]))
                    |> Map.ofSeq
            @>

    let integerValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%%(args.[0]) : Entity)
            // convert the value to int
            (entity.PropertyValue fieldTypeID) :?> int
            @>

    let dateTimeValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%%(args.[0]) : Entity)
            // convert the value to DateTime
            (entity.PropertyValue fieldTypeID) :?> System.DateTime
            @>

    let doubleValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%% args.[0] : Entity)
            // convert the value to double
            (entity.PropertyValue fieldTypeID) :?> double
            @>

    let booleanValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%% args.[0] : Entity)
            // convert the value to bool
            (entity.PropertyValue fieldTypeID) :?> bool
            @>

    let objValueExpression fieldTypeID =
        fun (args : Expr list) ->
        <@
            // get the entity
            let entity = (%%(args.[0]) : Entity)
            // convert the value to string
            (entity.PropertyValue fieldTypeID)
            @>

    // try to create a property name that does not conflict with anything else on the entity
    // Example: A Product entity with the field ProductCreatedBy would conflict with the Entity.CreatedBy property
    // Example: A Product entity with the field ProductAuthor and the field Author would conflict with each other
    let createPropertyName fieldTypeID =
        let result = fieldNamingConvention entityType.Id fieldTypeID
        // there is a property already on the Entity type matching this name
        let hasFixedProperty = typeof<Entity>.GetProperty(result) <> null
        // another field will become the same property
        let hasConflictingField = 
            entityType.FieldTypes 
            |> Seq.filter (fun ft -> fieldTypeID <> ft.Id)
            |> Seq.exists (fun ft -> result = (fieldNamingConvention ft.EntityTypeId ft.Id))
        
        if hasFixedProperty || hasConflictingField then
            // there is a conflict, return the original property name
            fieldTypeID
        else
            // there isn't a conflict, return the convention property
            result

    let fieldToProperty (fieldType : Objects.FieldType) =
        let fieldTypeID = fieldType.Id
        let propertyName = createPropertyName fieldTypeID

        // TODO Refactor this because it is ugly as F#ck
        if fieldType.Mandatory then
            // mandatory property
            match DataType.parse fieldType.DataType with
            | String as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((stringValueExpression fieldTypeID) >> uncheckedExpression))
            | LocaleString as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((localeStringValueExpression fieldTypeID) >> uncheckedExpression))
            | DateTime as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((dateTimeValueExpression fieldTypeID) >> uncheckedExpression))
            | Integer as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((integerValueExpression fieldTypeID) >> uncheckedExpression))
            | Boolean as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((booleanValueExpression fieldTypeID) >> uncheckedExpression))
            | Double as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((doubleValueExpression fieldTypeID) >> uncheckedExpression))
            // TODO Throw exception here when all the types have been handled
            | _ -> ProvidedProperty(propertyName, typeof<obj>, [], GetterCode = ((objValueExpression fieldTypeID) >> uncheckedExpression))
        else
            // optional property
            match DataType.parse fieldType.DataType with
            | String as dataType -> ProvidedProperty(propertyName, (DataType.toOptionType dataType), [], GetterCode = ((stringValueExpression fieldTypeID) >> optionPropertyExpression))
            | LocaleString as dataType -> ProvidedProperty(propertyName, (DataType.toType dataType), [], GetterCode = ((localeStringValueExpression fieldTypeID) >> uncheckedExpression))
            | DateTime as dataType -> ProvidedProperty(propertyName, (DataType.toOptionType dataType), [], GetterCode = ((dateTimeValueExpression fieldTypeID) >> optionPropertyExpression))
            | Integer as dataType -> ProvidedProperty(propertyName, (DataType.toOptionType dataType), [], GetterCode = ((integerValueExpression fieldTypeID) >> optionPropertyExpression))
            | Boolean as dataType -> ProvidedProperty(propertyName, (DataType.toOptionType dataType), [], GetterCode = ((booleanValueExpression fieldTypeID) >> optionPropertyExpression))
            | Double as dataType -> ProvidedProperty(propertyName, (DataType.toOptionType dataType), [], GetterCode = ((doubleValueExpression fieldTypeID) >> optionPropertyExpression))
            // TODO Throw exception here when all the types have been handled
            | _ -> ProvidedProperty(propertyName, typeof<Option<obj>>, [], GetterCode = ((objValueExpression fieldTypeID) >> optionPropertyExpression))

    member this.createProvidedTypeDefinition assembly ns =
        // create the type
        let typeDefinition = ProvidedTypeDefinition(assembly, ns, entityType.Id, Some typeof<Entity>)
        typeDefinition.HideObjectMethods <- true;

        // create a constructor
        let ctor = ProvidedConstructor(mandatoryProvidedParameters, InvokeCode = constructorExpression entityType.Id mandatoryFieldTypes)
        typeDefinition.AddMember ctor

        // creation method
        let createMethod = ProvidedMethod("Create", [ProvidedParameter("entity", typeof<Objects.Entity>)], typeDefinition)
        createMethod.IsStaticMethod <- true
        createMethod.InvokeCode <- (createExpression entityType.Id)
        typeDefinition.AddMember createMethod

        // save method
        let saveMethodReturnType = typedefof<Result<_,_>>.MakeGenericType([|(typeDefinition :> Type); typeof<Exception>|])
        let saveMethod = ProvidedMethod("Save", [ProvidedParameter("entity", typeDefinition)], saveMethodReturnType)
        saveMethod.IsStaticMethod <- true
        saveMethod.InvokeCode <- saveExpression
        typeDefinition.AddMember saveMethod

        // add fields as properties
        typeDefinition.AddMembers (entityType.FieldTypes |> Seq.map fieldToProperty |> Seq.toList)
        typeDefinition

[<TypeProvider>]
type InRiverProvider(config : TypeProviderConfig) as this =
    inherit TypeProviderForNamespaces()

    let ns = "inQuiry.Model"
    let assembly = Assembly.GetExecutingAssembly()
    
    // get the entity types from InRiver Model Service
    let entityTypes = 
        inRiverService.getEntityTypes() 
            |> Seq.map (fun et -> EntityTypeFactory(et).createProvidedTypeDefinition assembly ns)
            |> Seq.toList

    do
        this.AddNamespace(ns, entityTypes)

    
[<assembly:TypeProviderAssembly>] 
do()
