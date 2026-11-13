# Decisions Zitac S3 Module

> ⚠️ **Important:** Use this module at your own risk. See the **Disclaimer** section below.

## Overview

**Decisions Zitac S3 Module** connects the Decisions no-code automation platform to **any S3-compatible object storage**: MinIO, Ceph, NetApp StorageGRID, Dell ECS, Scality, Wasabi, Amazon S3 and others. It adds flow steps to upload, download, search, copy, move and delete files, read and change metadata and tags, and create presigned links.

It is built for the common case where nobody knows exactly which S3 product the storage runs on: the defaults work with as many S3 implementations as possible, features a server doesn't support fail with a clear "this S3 server does not support ..." message, and the **Check S3 Features** step shows what a server supports. Error messages say which field to check (e.g. "Check Credentials > Access Key ID"), because the people configuring the steps are usually not S3 experts.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Connection](#connection)
- [Flow Steps](#flow-steps)
- [Searching Files](#searching-files)
- [Metadata and Tags](#metadata-and-tags)
- [Tested S3 Servers](#tested-s3-servers)
- [Building from Source](#building-from-source)
- [Troubleshooting](#troubleshooting)
- [Known Limitations](#known-limitations)
- [Changelog](#changelog)
- [License](#license)
- [Disclaimer](#disclaimer)

## Features

### Works with any S3 server
- Server URL, region and access keys as a `Credentials` input, so the same flow runs against test and production
- Defaults that work with most S3 servers (path-style addresses, no AWS-only checksums); the less common settings are in a collapsed **Connection Settings** section, with explanations
- **Test S3 Connection** and **Check S3 Features** steps to set up a new server, including which S3 product it runs on (when the server reports it)
- Error messages with a hint which field to check

### Files
- **Upload File** with metadata and tags, content type detected from the file name
- **Download File**, **Get File Info**, **Delete File**, with an optional *Not Found* outcome for missing files
- **Copy or Move File**, also to change a file's metadata
- **Get Presigned URL**: a time-limited download or upload link that needs no credentials

### Search
- **Search Files** by prefix plus an **AND/OR filter tree**: file name, path, extension, size, date, storage class, content type, metadata and tags
- Filter values fixed in the designer or from step inputs
- Only files the cheap conditions can't decide get the extra metadata/tag requests, and those run in parallel
- Paging for large result sets

### Metadata and tags
- Metadata values with å, ä, ö and other non-ASCII characters (RFC 2047 encoding, decoded again when reading)
- **Get File Tags** / **Set File Tags** (replace or merge)

## Requirements

### Platform Requirements
- **Decisions Platform**: Version 9.26 or later, including 10.x. Tested on 9.32.0.75864 and 10.4.0.77190.
- **Decisions.AWS module**: Required and installed automatically. The AWS SDK libraries this module uses ship with it.

### S3 Requirements
- An S3-compatible storage, its server address, an access key with secret, and a bucket. The access key needs read access to the bucket for searching and downloading, and write access for uploading, copying, tagging and deleting.

## Installation

### Option 1: Install Pre-built Module
1. Download the compiled module (`Decisions.Zitac.S3.zip`)
2. Log into the Decisions Portal
3. Navigate to **System > Administration > Features**
4. Click **Install Module**
5. Upload the module file
6. Restart the Decisions service if prompted

### Option 2: Build and Install
Build the module as described in [Building from Source](#building-from-source), then install the generated `Decisions.Zitac.S3.zip` as in Option 1.

### Using the Module in a Project
In each project that should use the steps, add **Decisions.Zitac.S3** as a module dependency: open the project, go to **Manage > Configuration > Dependencies**, click **Manage**, select **Decisions.Zitac.S3** and click **Confirm**. The steps are then available in the Flow Designer under **Integration > S3 > Zitac**.

### Installing at Container Startup
When modules are installed at startup (`DECISIONS_MODULES_TO_INSTALL_ON_STARTUP` or `ModulesToInstallOnStartup.txt`), list `AWS` as well, e.g. `AWS,Zitac.S3`. Startup installation only orders the modules in the list; a dependency that isn't listed stops Decisions from booting with *"cannot run topological order in cyclic graph. Missing nodes: decisions.aws"*. Installing through **System > Administration > Features** installs the dependency automatically.

## Quick Start

1. Get from the S3 administrator: the **server address** (e.g. `https://s3.company.local:9000`), an **access key ID** and **secret access key**, and the **bucket** name. For Amazon S3, the region instead of the address.
2. In a test flow, add **Integration > S3 > Zitac > Test S3 Connection**. Build the **Credentials** input with Server URL and the two keys, set **Bucket**, and run it. If it fails, the error message says which field to check.
3. Run **Check S3 Features** once with the same inputs to see which features the server supports.
4. Use the same Credentials in the other steps, e.g. from a flow variable or a sub-flow that builds them per environment. Keep the secret out of flow constants where possible. Leave the connection settings at their defaults unless a step fails or the S3 administrator says otherwise.

## Connection

There are no module settings: every step gets the connection as input, so the module works the same on Decisions 9.x and 10.x and a flow can use any number of S3 servers. The connection is split by what changes when.

### Credentials (input)
`S3Credentials`, an input on every step: what typically differs between environments.

| Field | Empty means | Notes |
|---|---|---|
| Server URL | Amazon S3 | e.g. `https://s3.company.local:9000`. Use exactly the address the S3 administrator gives you (see [Troubleshooting](#troubleshooting)). |
| Region | `us-east-1` | Leave empty unless the S3 administrator gives one. Required for Amazon S3. |
| Access Key ID / Secret Access Key | anonymous access | The secret is a password field and is masked in the debugger. |

### Connection Settings (step properties)
How to talk to the server, in the collapsed **Connection Settings** section of every step. The defaults work with most S3 servers, and each setting that needs explaining has a note in the designer. Tick **Provide Settings as Input** to get a `Connection Settings` input (`S3ConnectionSettings`) instead, e.g. one flow variable shared by many steps; fields left empty there use the defaults.

| Setting | Default | Change when |
|---|---|---|
| Use Virtual Hosted Style | off: path-style addresses (`server/bucket/file`) | The S3 administrator says the server needs `bucket.server` addresses |
| Use AWS Checksums | off: no AWS SDK v4 default checksums and chunked uploads, which many non-AWS servers reject | Only for Amazon S3 or a server known to support them |
| List API Version | V2 | Check S3 Features says V2 fails and V1 works |
| Accept Untrusted Certificate | off | Last resort for certificate errors (a warning is shown while it is on). Better: trust the server's certificate or internal CA on the Decisions server |
| Timeout Seconds | 100 | Large files over slow connections |
| Metadata Encoding | Rfc2047 | See [Metadata and Tags](#metadata-and-tags) |

## Flow Steps

All steps are under **Integration > S3 > Zitac**, take **Credentials** and **Bucket** as inputs (Bucket is optional for Test S3 Connection), and have an **Error** outcome with a message that says what to check.

**Not Found outcome:** Get File Info always has a **Not Found** outcome. Download File, Delete File, Copy or Move File (missing source), Get File Tags and Set File Tags get one when **Show Outcome For Not Found** is ticked (in the step's *Outcomes* section); otherwise a missing file goes to Error. A wrong bucket always goes to Error. S3 reports a delete as successful even when the file doesn't exist, so Delete File checks first (one extra request) when the outcome is on.

| Step | What it does | Works on every S3 server? |
|---|---|---|
| Upload File | Uploads a file with optional metadata and tags; content type detected from the file name if not given | Yes; tags only where supported |
| Download File | Downloads a file with its info and metadata | Yes |
| Get File Info | Size, dates, content type, metadata, optionally tags; **Not Found** outcome for missing files | Yes; tags only where supported |
| Search Files | Lists files under a prefix and filters them, see [Searching Files](#searching-files) | Yes; tag conditions only where supported |
| Copy or Move File | Server-side copy (max 5 GB), optionally deleting the source | Yes |
| Delete File | Deletes a file; a missing file counts as deleted unless Show Outcome For Not Found is on | Yes |
| Get File Tags / Set File Tags | Reads, replaces or merges tags | Only where tags are supported |
| Get Presigned URL | Time-limited download or upload link | Any server with standard (SigV4) signing |
| Test S3 Connection | Checks the credentials and the bucket, lists the buckets if allowed, reports the server software | Yes |
| Check S3 Features | Shows which features the server supports | Yes |

### Copy or Move File
**Metadata Handling** = *Keep* (copy as is), *Merge* (source metadata plus the given entries) or *Replace* (only the given entries). Copying a file onto itself with Merge or Replace is how metadata is changed after upload. Content-Type and the other stored headers are carried over, because S3 would otherwise drop them. **Delete Source (Move)** copies and then deletes the source; this is not atomic, so if the delete fails, the copy stays and the step reports it.

### Get Presigned URL
A link valid for up to 7 days to download (GET) or upload (PUT) one file without credentials. It is calculated locally, so it isn't checked that the file exists. The signature includes the host name: the link only works where the Server URL is reachable under the same name. **Download File Name** makes browsers save the file under that name (non-ASCII names supported).

### Test S3 Connection
With a **Bucket** (recommended), the step succeeds if that bucket can be read. **Buckets** is also filled if the credentials may list all buckets; keys limited to one bucket often may not, and **Message** then says so. Without a Bucket, the step lists the buckets.

### Check S3 Features
Writes, reads and deletes small temporary files under `decisions-s3-feature-check/` in the bucket, so the credentials need write access. It goes to **Error** when the bucket can't be reached at all (wrong bucket, credentials or server). Otherwise **Done** with **All Supported**, **Unsupported Features**, a readable **Report**, **Server Software** and one **Results** row per feature (listing, upload with metadata, non-ASCII metadata, tags, copy, delete, versioning).

## Searching Files

### Prefix
S3 has no real folders: every file has one full name, its *key*, e.g. `invoices/2026/customer-123/invoice-456.pdf`. The **Prefix** is the start of the key: `invoices/2026/` is that "folder" and everything below it, and it can also end in the middle of a name (`invoices/2026/inv-12`). It is the **only filter the S3 server applies itself**, so it decides how many files have to be listed and checked: make it as narrow as possible. With **Include Subfolders** off, only files directly in the prefix "folder" are returned, and the subfolders are listed in **Folders**.

### Filters
The **Filters** property is a list of nodes, like in Fetch Entities (Advanced). Each node is a **Filter** (one condition) or an **And** / **Or** group of nodes, nested as deep as needed. The top-level list is combined with AND. No filters returns all files under the prefix.

| Field | Cost | Operators |
|---|---|---|
| File Name, Key (Full Path), Extension, Storage Class | free (part of the file list) | Equals, Not Equals, Contains, Does Not Contain, Starts With, Ends With, Matches Wildcard (`*` `?`), In List, Not In List |
| Size (Bytes) | free | Equals, Not Equals, Greater Than, Greater Or Equal, Less Than, Less Or Equal |
| Last Modified | free | Before, After, Older Than (Days), Newer Than (Days) |
| Content Type | one request per undecided file (shared with Metadata) | text operators |
| Metadata (with Key Name) | one request per undecided file | text operators, Exists, Does Not Exist |
| Tag (with Key Name) | one more request per undecided file; the server must support tags | text operators, Exists, Does Not Exist |

- **Values** are fixed in the designer or come from a **Step Input**: the step then gets an input with that name and type. A step input that is empty at runtime makes the condition count as true (it is skipped).
- Text comparisons are case-insensitive unless **Case Sensitive** is ticked. Extension works with or without the dot.
- A metadata or tag key the file doesn't have only matches the negative operators (Not Equals, Does Not Contain, Not In List, Does Not Exist).
- Key (Full Path) filters are checked by the module after listing. For a fixed start of the path, use the Prefix instead: it is much faster.

### How it stays fast
Every file is first checked with the free fields only. Conditions on metadata, content type and tags can't be decided at that point, and only those undecided files get the extra request(s); tags are fetched only if metadata didn't already decide. Measured on 2,500 files: `File Name ends with 5.txt AND Metadata city = Göteborg` made 250 extra requests, the same with OR 2,250, and filters without metadata or tag conditions none.

The extra requests run **Parallel Requests** at a time (default 10), at most **Max Files To Inspect** per call (default 1000); both are in the collapsed **Performance** section.

### Results and paging
Results contain what the file list has (key, name, size, last modified, ETag, storage class) plus whatever the filters fetched. **Include Metadata In Results** and **Include Tags In Results** fetch them for every returned file (one request each per file).

At most **Max Results** files and folders are returned per call. If **Has More** is true, call the step again with **Continue From** set to **Continue From Next** to get the next batch; leave Continue From empty for the first call.

### Large buckets
Searching metadata over many thousands of files gets slow and costly. Put what you search on into the path (`invoices/2026/customer-123/...` makes "2026 invoices for customer 123" a single prefix search), or keep an index in Decisions: store the key and the searchable values on upload, search there, and use Download File or Get File Info for the hits. Some S3 products have their own metadata search (MinIO, StorageGRID, Amazon S3 Metadata tables); these are product-specific and not used by this module.

## Metadata and Tags

**Metadata** (`x-amz-meta-*`) is set at upload and is supported by every S3 server. It can only be changed by copying the file onto itself (Copy or Move File with Merge or Replace). Keys may contain `a-z`, `0-9`, `-` and `_`. Servers limit the total size (2 KB per file on Amazon S3), and encoded non-ASCII values take more space than they look.

**Tags** (max 10 per file) can be changed at any time, but not every S3 server supports them. Allowed characters are letters, numbers, spaces and `+ - = . _ : / @`; some servers (e.g. MinIO) don't accept å, ä, ö in tags, so store such values as metadata. Some servers silently ignore tags sent with Upload File: Check S3 Features shows whether tags work.

### Non-ASCII metadata values
Metadata travels as HTTP headers, which are only reliably ASCII, so values like `Göteborg` are encoded according to the **Metadata Encoding** connection setting:

- **Rfc2047** (default): only values with non-ASCII or control characters are encoded, e.g. `=?UTF-8?B?R8O2dGVib3Jn?=`; plain ASCII values are stored unchanged. This is the standard for non-ASCII text in headers, and Amazon S3 uses it too, so other tools are most likely to understand it.
- **UrlEncode**: every value is percent-encoded (`G%C3%B6teborg`, also `a b` becomes `a%20b`).
- **Reject**: ASCII values only.

The steps decode the values again when reading (Get File Info, Download File, Search Files), so flows always see the real text. Use the same setting in all flows that read or write the same files. Check S3 Features tests a non-ASCII value with the chosen encoding.

## Tested S3 Servers

All steps tested with the default connection settings (2026-11-13, version 1.0.0):

| Server | Result |
|---|---|
| Ceph Object Gateway (squid) | Everything works |
| MinIO (current source build) | Everything works; tag values can't contain å, ä, ö or `,` |
| SeaweedFS 4.x | Everything works; tag values can't contain `,` |
| Zenko CloudServer (Scality) | Everything works |
| Garage 2.5 | Everything except tags: not supported (clear error message), and tags sent with Upload File are silently ignored. Needs Region `garage`. |

With **Use AWS Checksums** turned on, uploads fail on Garage and Zenko, which is why it is off by default.

## Building from Source

### Prerequisites
- .NET 10 SDK
- CreateDecisionsModule Global Tool (`dotnet tool install --global CreateDecisionsModule-GlobalTool`)

The project compiles against the Decisions SDK (NuGet) and the DLLs in `lib/`. Those are copies of the AWS SDK DLLs that already ship with Decisions (core and the Decisions.AWS module). They are compile-only references and are never packaged into the module.

### Build Steps

#### On Linux/macOS:
```bash
chmod +x build_module.sh
./build_module.sh
```

#### Manual Build:
```bash
# 1. Publish the project
dotnet publish ./Decisions.Zitac.S3/Decisions.Zitac.S3.csproj --runtime linux-x64 --self-contained false --output ./Decisions.Zitac.S3/bin -c Release

# 2. Create the module package
dotnet msbuild build.proj -t:build_module
```

### Build Output
`Decisions.Zitac.S3.zip` in the repository root, containing only `Decisions.Zitac.S3.dll` and the module icon.

> ⚠️ Build the package with `build.proj` (or `build_module.sh`), not by calling `CreateDecisionsModule` directly. `build.proj` handles three things:
> - The tool cuts the module name at the last dot (`Decisions.Zitac.S3` would become `Decisions.Zitac`). `build.proj` passes `Decisions.Zitac.S3.Module` and renames the zip.
> - The tool requires a folder `../Decisions.AWS` (the dependency's name) next to the project. `build.proj` creates an empty one if needed and removes it afterwards.
> - The tool (version 2.2.0) targets .NET 8. `build.proj` runs it with `DOTNET_ROLL_FORWARD=Major`, so the .NET 10 runtime is enough.

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Every call fails with **NoSuchBucket**, but the bucket exists | Use exactly the server address the S3 administrator gave you. Some servers (e.g. Ceph) treat an unknown host name, such as an alias, as a bucket name. |
| **InvalidURI** "Check your restEndpoints configuration" | The server (Zenko/Scality) doesn't accept this host name. Use its configured address. |
| **SignatureDoesNotMatch**, or AccessDenied "Invalid signature" | Wrong secret access key, or the server needs a specific **Region** (e.g. Garage: `garage`). |
| **AccessDenied** "no access key was given" | The Credentials input is not mapped or its keys are empty. |
| Certificate (TLS) errors | Have the server's certificate or internal CA trusted on the Decisions server. As a last resort, turn on **Accept Untrusted Certificate**. |
| Uploads fail with checksum or "invalid payload signature" errors | Make sure **Use AWS Checksums** is off. |
| "This S3 server does not support object tagging" | The server has no tag support (e.g. Garage). Use metadata instead. |
| **InvalidTag** | Tag values contain characters the server doesn't accept (e.g. å, ä, ö on MinIO). Store such values as metadata. |
| Steps can't be found in the toolbox | Add **Decisions.Zitac.S3** as a dependency of the project. |
| Decisions stops booting with "Missing nodes: decisions.aws" | The module is installed at container startup without `AWS` in the list, see [Installing at Container Startup](#installing-at-container-startup). |

## Known Limitations

- Upload and download hold the whole file in memory, max 5 GB (no multipart upload).
- Copy or Move File handles files up to 5 GB (no multipart copy).
- S3 has no server-side search on metadata or tags; see [Large buckets](#large-buckets).

## Changelog

### 1.0.0
- First version: Upload File, Download File, Get File Info, Search Files (AND/OR filter tree), Copy or Move File, Delete File, Get/Set File Tags, Get Presigned URL, Test S3 Connection, Check S3 Features
- Connection as inputs (Credentials) plus step-level connection settings, with defaults for S3-compatible servers
- RFC 2047 encoding for non-ASCII metadata values
- Runs on Decisions 9.26 and later, including 10.x, from one build

## License

This module is licensed under the [MIT License](LICENSE). Copyright (c) 2024-2026 Zitac Consulting AB.

## Disclaimer

This module is provided "as is" without warranties of any kind. Use it at your own risk. The authors, maintainers, and contributors disclaim all liability for any direct, indirect, incidental, special, or consequential damages, including data loss or service interruption, arising from the use of this software.

**Important Notes:**
- Always test in a non-production environment first
- Use access keys with only the permissions the flows need, and keep the secret out of flow constants where possible
- Delete File, Move and Set File Tags change data in the bucket; make sure versioning or backups are in place where needed
- This module is not officially supported by Amazon Web Services, any S3 storage vendor, or Decisions
