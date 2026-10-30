## Purpose

Define deterministic ownership and cleanup behavior so failed initialization, retries, session stops, and application exit cannot leave stale browser data, active subscriptions, unobserved tasks, or undisposed operating-system resources.

## ADDED Requirements

### Requirement: Runtime cleanup continues after individual failures

The system SHALL attempt every cleanup step registered for a runtime session even when one or more earlier steps fail. It SHALL revoke the session, detach event handlers, clear published ownership, dispose all captured owners, and report the collected failures without exposing credentials.

#### Scenario: One cleanup owner throws
- **WHEN** a session stop or application close encounters an exception while disposing one runtime owner
- **THEN** every remaining captured owner is still given one disposal attempt
- **AND** the session object is disposed and removed from current application ownership
- **AND** the cleanup result records the failing stage without recording a PIN, page body, or credential-bearing URL

#### Scenario: Cleanup is requested repeatedly
- **WHEN** stop, retry, or close requests cleanup more than once for the same session
- **THEN** all callers join one idempotent cleanup operation
- **AND** no resource is re-created or disposed concurrently by a second cleanup operation

### Requirement: Browser session directories have a terminal cleanup path

The system SHALL assign every created browser session directory either to a live browser environment or to a retired state that is eligible for cleanup. Initialization failure, cancellation, disposal, and browser-process exit MUST all lead to a terminal retirement path without deleting a directory still used by a live browser process.

#### Scenario: Environment creation fails
- **WHEN** a browser session directory is created and browser environment creation fails before returning an environment
- **THEN** the directory is marked retired and cleanup is attempted
- **AND** a later startup can remove it if an immediate delete is not possible

#### Scenario: Initialization is cancelled after environment creation
- **WHEN** cancellation or disposal wins after a browser environment has been created
- **THEN** the system releases the controller and waits for or observes the owned browser process exit before retiring the directory
- **AND** no active browser profile is deleted prematurely

#### Scenario: Retired data remains locked temporarily
- **WHEN** a retired session directory cannot be deleted during the current process lifetime
- **THEN** the failure does not keep the session active
- **AND** a later bounded cleanup pass retries only directories already marked retired

### Requirement: Device monitors release all owned resources

The system SHALL stop device-change sources, detach callbacks, settle or observe tracked refresh work, dispose cancellation sources and synchronization primitives, and dispose the change source on every shutdown path. A stop failure or refresh-task fault MUST NOT skip later release stages.

#### Scenario: Device change source stop fails
- **WHEN** the underlying change source throws while stopping
- **THEN** callbacks are detached and all remaining monitor-owned resources are still disposed
- **AND** the stop failure is observed and reported after cleanup completes

#### Scenario: A tracked refresh task faults
- **WHEN** enumeration or subscriber delivery faults in tracked background work
- **THEN** the fault is observed exactly once and does not become an unobserved task exception
- **AND** monitor disposal still reaches a terminal state

#### Scenario: Notification arrives after disposal begins
- **WHEN** a device notification races with monitor disposal
- **THEN** no new refresh or external callback is published after disposal ownership has been revoked

### Requirement: Asynchronous subscribers are isolated

The system SHALL invoke a stable snapshot of asynchronous event subscribers and isolate each subscriber's failure so one handler cannot prevent later handlers from receiving the same event. Subscriber failures SHALL be observed and reported without escaping through native or fire-and-forget event boundaries.

#### Scenario: First USB subscriber throws
- **WHEN** the first USB device-change subscriber throws or returns a faulted task
- **THEN** remaining subscribers still receive the same device snapshot
- **AND** the failed delivery is observed without terminating the monitor

#### Scenario: Session subscriber throws
- **WHEN** a business-session device subscriber fails while the session is still current
- **THEN** later current-session subscribers are still invoked
- **AND** the failure cannot restore or mutate an ended session

### Requirement: Long-lived services have explicit bounded ownership

The system SHALL define an owner and terminal disposal path for long-lived services, synchronization primitives, event subscriptions, network clients, cancellation sources, and in-memory registries. Repeated update checks and downloads MUST NOT cause registries of obsolete releases or paths to grow without bound.

#### Scenario: Application closes during an update request
- **WHEN** the application closes while an update check, download, or verification is pending
- **THEN** the operation is cancelled or allowed to settle within the existing exit budget
- **AND** no late callback updates the closed UI
- **AND** all application-owned update resources and subscriptions are released

#### Scenario: Many update candidates are processed
- **WHEN** repeated checks and downloads replace previously selected releases or cached paths
- **THEN** entries that are no longer actionable are removed or bounded by a documented policy
- **AND** memory retained by the update service does not grow in proportion to the lifetime number of checks

#### Scenario: A subscribed command object is disposed
- **WHEN** an owner releases a command or presentation object that subscribed to another long-lived component
- **THEN** the subscription is detached
- **AND** later source events do not retain or call the released object

