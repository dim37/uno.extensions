---
uid: Overview.Mvux.Advanced.Messaging
---

# Messaging

Messaging is the ability to send in-app messages between its components, in a way that enables them to remain coupled from one another.

## Community Toolkit messenger

The Community Toolkit messenger can be used as a central tool to send and receive global messages between objects in the app. The advantage of the messenger is that objects can remain decoupled from each other as the messenger enables sending the messages around without keeping a strong reference between the sender and the receiver. The messages can also be sent over specific channels uniquely identified by a token or within certain areas of the application.

MVUX includes extension methods that enable the integration between the [Community Toolkit messenger](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/messenger) and [MVUX feeds](xref:Overview.Mvux.Feeds). But before discussing how MVUX integrates with the Community Toolkit messenger, let's have a quick look on how the messenger works.

The core component of the messenger is the `IMessenger` object. Its main methods are `Register` and `Send`. `Register` subscribes to an object to start listening to messages of a certain type, whereas `Send` sends out messages to all listening parties.  
There are various ways to obtain the `IMessenger` object, but we'll use the most common one, which involves using [Dependency Injection](xref:Overview.DependencyInjection) (DI) to register the `IMessenger` service in the app so it can then be resolved at the construction of other dependent types (e.g., ViewModels).

In the model, we obtain a reference to the `IMessenger` on the constructor, which is resolved by the DI's service provider.
The `Register` method has quite a few overloads, but for the sake of this example, let's use the one that takes the recipient and the message type as parameters. The first parameter is the recipient (`this` in this case), and the second one is a callback that is executed when a message has been received. Although `this` can be called within the callback, it's preferred that the callback doesn't make external references and that the `MyModel` is passed in as an argument.  
`MessageReceived` is then called on the received recipient (which is the current `MyModel`), and the message is passed into it:

```csharp
using CommunityToolkit.Mvvm.Messaging;

public partial record MyModel
{
    protected IMessenger Messenger { get; }
    
    public MyModel(IMessenger messenger)
    {
        this.Messenger = messenger;

        this.Messenger.Register<MyModel, MyMessage>(this, (recipient, myMessage) =>
        {
            recipient.MessageReceived(myMessage);
        });
    }

    public void MessageReceived(MyMessage myMessage)
    {
        ...
    }
}
```

The `MyMessage` type is defined in a location accessible to both the sender and the receiver:

```csharp
public record MyMessage(string Message);
```

Then, in another location or module in the app, a `MyMessage` is sent:

```csharp
using CommunityToolkit.Mvvm.Messaging;

public partial record AnotherModel
{
    public AnotherModel(IMessenger messenger)
    {
        this.Messenger = messenger;
    }

    protected IMessenger Messenger { get; }

    public IFeed<string> Message => ...

    public void SendMessage(string message)
    {
        var myMessenge = new MyMessage(message);
        Messenger.Send(myMessage);
    }
}
```

## MVUX integration with the messenger

MVUX can pull information from services, but often there needs to be a way to get notifications from the service of entity changes. This is when messaging can come handy.

MVUX harnesses the power of the Community Toolkit messenger and adds extension methods that enable you to listen to entity changes received from the messenger and have them automatically applied to the state or list-state storing the entities in the current model. The following entity-change types are supported: created, updated, and deleted.

For instance, when there is a command in the model that, when executed, creates a new entity and stores it in the database using a service, the service can send a 'created' entity-change message with the messenger, which can then be intercepted in the model to have the state or list-state update itself and display the newly created entity received from the messenger.

These extensions are shipped in the [`Uno.Extensions.Reactive.Messaging`](https://www.nuget.org/packages/Uno.Extensions.Reactive.Messaging) NuGet package.

### Observe

The purpose of the `Observe` methods (it comes in several overloads, [see below](#additional-observe-overloads)), is to intercept entity-change messages (`EntityMessage<T>`) from the Community Toolkit messenger and apply them to the designated state or list-state.

In the example below, there is a model that displays a state-list of `Person` entities received from a service, loaded using a state, with the [`Async`](xref:Overview.Mvux.ListStates#async) factory method.

As you can gather from the code, the service interacts with an external source to load and save `Person` data. In the example below, we can see the use of two of its methods: `GetAllAsync` and `CreateNameAsync`.

There's also the `CreateNewPerson` method, which gets generated as a command in the bindable proxy, to be invoked from the View (refer to [commands](xref:Overview.Mvux.Advanced.Commands) to learn about how MVUX generates commands). This method uses `CreateRandomName`, which generates a random name. Its implementation has been removed for brevity.

The line using the MVUX messaging extension method is the one calling `messenger.Observe`. Read the code, and this line will be explained thereafter.

```csharp
using CommunityToolkit.Mvvm.Messaging;
using Uno.Extensions.Reactive;
using Uno.Extensions.Reactive.Messaging;

public record Person(string Name);

public partial record PeopleModel
{
    protected IPeopleService PeopleService { get; }

    public IListState<Person> People => ListState.Async(this, PeopleService.GetAllAsync);

    public PeopleModel(IPeopleService peopleService, IMessenger messenger)
    {
        PeopleService = peopleService;

        messenger.Observe(state: People, keySelector: person => person.Name);
    }
    
    public async TaskValue CreateNewPerson(CancellationToken ct)
    {
        var randomName = CreateRandomName();
        var newPerson = new Person(randomName);

        await PeopleService.CreateAsync(newPerson);
    }

    public static string CreateRandomName()
    {
        ...
    }
}
```

The `Observe` method in the model code subscribes the `People` state to the messenger's entity-change messages (`EntityMessage<Person>`).

An `EntityMessage<T>` carries an `EntityChange` enum value which indicates its type of change (`Created`, `Updated`, and `Deleted`), as well as the actual entity that was changed.

These messages are sent in the service upon successful creation of a `Person`, signaling the model to update itself with the new data. This is indeed automatically reflected in the `People` list-state, which adds the newly created `Person`.

The service's code looks like the following:

```csharp
public class PeopleService : IPeopleService
{
    protected IPeopleRepository PeopleRepository { get; }
    protected IMessenger Messenger { get; }

    public PeopleService(IPeopleRepository peopleRepository, IMessenger messenger)
    {
        PeopleRepository = peopleRepository;
        Messenger = messenger;
    }

    public async ValueTask<IImmutableList<Person>> GetAllAsync(CancellationToken ct)
    {
        var allPeople = await PeopleRepository.GetAllPeople(ct);

        return allPeople.ToImmutableList();
    }

    public async ValueTask CreateAsync(Person person, CancellationToken ct)
    {
        var createdPerson = await PeopleRepository.CreateAsync(person, ct);

        Messenger.Send(new EntityMessage<Person>(EntityChange.Created, createdPerson));
    }
}
```

As you can see, the messenger's `Send` method is called in the `CreateAsync` call, specifying a 'created' `EntityMessage` along with the newly created person. The model observes this type of message `EntityMessage<Person>`, and as the `People` list-state has been subscribed to this, it will be updated with the newly created person added to it.

<!--TODO link from IState IListState page -->

#### Additional Observe overloads

The `Observe` extension method comes in several flavors.
They all share a common goal - to send and intercept entity-message messages to and from the messenger and apply them to a state or feed-state. They also share a `keySelector` parameter which uses to determine by which property the entity is identified. This is important so that the state can compare or look up an appropriate entity where needed; for example, when an entity is updated, it will replace the old one with the new one received with the entity message.

- `Observe<TEntity, TKey>(IState<TEntity> state, Func<TEntity, TKey> keySelector)`

    This overload takes the state to apply the entity messages on and the key-selector that determines by which property this entity is identified.
    It is designated for single-value states (`IState<T>`) and will update the state if an entity message has been received about an update or a deletion of an entity that is equal to the one currently present in the state.

    In the following example, if any of the details of the current user change and an update entity message has been sent with the messenger about the change, the `CurrentUser` state will update the `User` entity to reflect those changes.    

    ```csharp
    public partial record MyModel
    {
        protected IUserService UserService { get; }

        public MyModel(IUserService userService)
        {
            UserService = userService;
            
            messenger.Observe(CurrentUser, user => user.Id);            
        }

        public IState<User> CurrentUser => State.Async(this, UserService.GetCurrentUser);
    }
    ```

    The same applies to a deleted entity - the state will clear the current value.

- `Observe<TEntity, TKey>(IListState<TEntity> listState, Func<TEntity, TKey> keySelector)`

    This overload is similar to the previous one, except it takes a list-state and applies the entity messages on it, using the key-selector parameter to determine by which property this entity is identified with for comparison purposes.
    As long as entity-messages are sent from the service, the list-state will remain in sync and reflect newly added, deleted, or updated entities.   

    This method is also the one that was used in the sample code above:

    ```csharp
    public partial record PeopleModel
    {
        protected IPeopleService PeopleService { get; }

        public PeopleModel(IPeopleService peopleService, IMessenger messenger)
        {
            PeopleService = peopleService;

            messenger.Observe(state: People, keySelector: person => person.Name);
        }

        public IListState<Person> People => ListState.Async(this, PeopleService.GetAllAsync);
    }
    ```

- `Observe<TOther, TEntity, TKey>(IListState<TEntity> listState, IFeed<TOther> other, Func<TOther, TEntity, bool> predicate, Func<TEntity, TKey> keySelector)`

This overload intercepts entity-change messages from the messenger for a certain entity type but only refreshes the state when the predicate returns `true` based on related entities from another feed.

Using the previous example, if each `Person` has a list of `Phone` with a `Phone.PersonId` property associating them to their owning `Person`, making changes to a `Phone` (e.g., removing one), will have the service send an entity-change message which will refresh the `SelectedPersonPhones` list-state, but only if the `Phone.PersonId` matches with the currently selected person `Id`:

```csharp
public partial record PeopleModel
{
    protected IPeopleService PeopleService { get; }
    protected IPhoneService PhoneService { get; }

    public PeopleModel(IPeopleService peopleService, IPhoneService phoneService, IMessenger messenger)
    {
        PeopleService = peopleService;
        PhoneService = phoneService;

        messenger.Observe(People, person => person.Id);
        messenger.Observe(SelectedPersonPhones, SelectedPerson, (person, phones) => true, person => person.Id);
    }

    public IListState<Person> People =>
        ListState
        .Async(this, PeopleService.GetPeople)
        .Selection(SelectedPerson);

    public IState<Person> NewPerson => State<Person>.Value(this, Person.EmptyPerson);

    public IState<Person> SelectedPerson => State<Person>.Empty(this);

    public IListState<Phone> SelectedPersonPhones => ListState.FromFeed(this, SelectedPerson.SelectAsync(GetAllPhonesSafe).AsListFeed());

    private async ValueTask<IImmutableList<Phone>> GetAllPhonesSafe(Person selectedPerson, CancellationToken ct)
    {
        if (selectedPerson == null)
            return ImmutableList<Phone>.Empty;

        return await PhoneService.GetAllPhones(selectedPerson, ct);
    }

    public async ValueTask AddPerson(CancellationToken ct = default)
    {
        var newPerson = (await NewPerson)!;

        await PeopleService.AddPerson(newPerson, ct);

        await NewPerson.Update(old => Person.EmptyPerson(), ct);
    }

    public async ValueTask RemovePhone(int phoneId, CancellationToken ct)
    {
        await PhoneService.DeletePhoneAsync(phoneId, ct);
    }
}
```

The `SelectedPersonPhone` state will only be refreshed if it falls under the predicate criteria, which is limited to the currently selected `Person`.

> ![NOTE]
> The `Selection` method above picks up UI selection changes and reflects them onto a state. This subject is covered [here](xref:Overview.Mvux.Advanced.Selection).

- `Observe<TOther, TEntity, TKey>(IState<TEntity> state, IFeed<TOther> other, Func<TOther, TEntity, bool> predicate, Func<TEntity, TKey> keySelector)`

This overload is the same as the previous one, except it watches a single-item state rather than a list-state, as in the previous example.

### Update

The MVUX messaging package also includes a pair of `Update` methods that enable updating an `IState<T>` or an `IListState<T>` from an `EntityMessage<T>`.
The main purpose of these messages is to serve the aforementioned `Observe` extension methods, but they can be used otherwise, for example, if you would like to create additional implementations of the `Observe` extension methods.

These methods apply data from an `EntityMessage<T>` to an `IState<T>` or an `IListState<T>`. So for example, if an entity-message contains a `created` `T` entity, applying this entity-message to a list-state will add that record to the list-state. The same applies to updating or removing.
