## ADDED Requirements

### Requirement: Select the application directory before installation
The installer SHALL present a Chinese installation directory page before installing payload or changing native authentication components. A fresh installation SHALL default to the previous per-user path and allow another writable local fixed-drive application subdirectory, including spaces and Chinese characters.

#### Scenario: Fresh user chooses another drive
- **WHEN** no installation is registered and the user selects a valid directory then confirms installation
- **THEN** payload, shortcut, uninstall metadata and authentication binding use the selected application directory

#### Scenario: User cancels at directory selection
- **WHEN** the user cancels before confirming installation
- **THEN** the installer does not install application files or mutate authentication components

### Requirement: Retain installation location during upgrades
The installer SHALL resolve the existing product location from its stable HKCU installation registration and display that location during upgrades. It SHALL reject relocation of an existing installation, explaining that the user can uninstall first and select another directory on fresh installation. Existing default-directory installations SHALL remain supported.

#### Scenario: Update an application on another drive
- **WHEN** a registered custom-directory installation receives an update
- **THEN** the update retains that directory and binds the new files there, preserving user settings and PDFs

#### Scenario: Existing user tries to relocate
- **WHEN** an existing installation is present and another target is supplied
- **THEN** the installer blocks before any component or payload mutation and explains the uninstall-first route

### Requirement: Validate chosen paths before mutation
The installer SHALL reject unsupported or unsafe paths before payload installation and recheck them at the transaction entry point. The checks SHALL cover roots, system and user-data locations, relative or network/device paths, reparse-point traversal, unwritable parent locations and unowned files. Recovery and uninstall SHALL retain their existing file-ownership boundaries.

#### Scenario: Target contains personal files
- **WHEN** a proposed directory contains files not owned by this application
- **THEN** installation is blocked and those files remain unchanged

#### Scenario: Application directory lies beneath a junction
- **WHEN** an ancestor redirects the chosen path
- **THEN** installation is blocked before following it for mutation

### Requirement: Explain custom installation in the updater
The update interface SHALL describe the directory selection and upgrade reuse rules without claiming the default directory is the only target or misclassifying a custom installation as a development build.

#### Scenario: User updates from a custom directory
- **WHEN** the user opens the update window
- **THEN** it displays the current program location and explains that the installer retains the registered installation directory
