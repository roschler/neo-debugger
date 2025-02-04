// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { WorkspaceFolder, DebugConfiguration, ProviderResult, CancellationToken } from 'vscode';
import { join, basename, relative, resolve, extname } from 'path';
import * as fs from 'fs';
import * as os from 'os';
import * as cp from 'child_process';
import * as _glob from 'glob';
import slash = require('slash');

function checkFileExists(filePath: string): Promise<boolean> {
    return new Promise((resolve, reject) => {
        fs.stat(filePath, (err, stats) => {
            if (stats && stats.isFile()) {
                resolve(true);
            } else {
                resolve(false);
            }
        });
    });
}

function deleteFile(filePath: fs.PathLike): Promise<void> {
    return new Promise((resolve, reject) => {
        fs.unlink(filePath, (err) => {
            if (err) {
                reject(err);
            } else {
                resolve();
            }
        });
    });
}

function execChildProcess(command: string, workingDirectory: string): Promise<string> {
    return new Promise<string>((resolve, reject) => {
        cp.exec(command, { cwd: workingDirectory, maxBuffer: 500 * 1024 }, (error, stdout, stderr) => {
            if (error) {
                reject(error);
            }
            else if (stderr && stderr.length > 0) {
                reject(new Error(stderr));
            }
            else {
                resolve(stdout);
            }
        });
    });
}

function glob(pattern:string, options: _glob.IOptions = {}) : Promise<string[]> {
    return new Promise((resolve, reject) => {
        _glob(pattern, options, (err, matches) => {
            if (err) {
                reject(err);
            } else {
                resolve(matches);
            }
        });
    });
}

// this method is called when your extension is activated
// your extension is activated the very first time the command is executed
export async function activate(context: vscode.ExtensionContext) {

    let neoDebugChannel = vscode.window.createOutputChannel('Neo Debugger Log');

    const configProvider = new NeoContractDebugConfigurationProvider();
    context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider("neo-contract", configProvider));

    const factory = new NeoContractDebugAdapterDescriptorFactory(neoDebugChannel, context.extensionMode);
    context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory("neo-contract", factory));

    context.subscriptions.push(vscode.commands.registerCommand("neo-debugger.displaySourceView",
        async () => await changeDebugView("source")));
    context.subscriptions.push(vscode.commands.registerCommand("neo-debugger.displayDisassemblyView",
        async () => await changeDebugView("disassembly")));
    context.subscriptions.push(vscode.commands.registerCommand("neo-debugger.toggleDebugView", 
        async () => await changeDebugView("toggle")));
}

class DebugViewSettings {
    debugView: 'source' | 'disassembly' | 'toggle' = 'source';
}

async function changeDebugView(debugView: 'source' | 'disassembly' | 'toggle') {
    const settings: DebugViewSettings =  {
        debugView: debugView
    };

    if (vscode.debug.activeDebugSession && vscode.debug.activeDebugSession.type === 'neo-contract') {
        await vscode.debug.activeDebugSession.customRequest("debugview", settings);
    }
}

class NeoContractDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
    public async provideDebugConfigurations(folder: WorkspaceFolder | undefined, token?: CancellationToken): Promise<DebugConfiguration[]> {

        function createConfig(programPath: string | undefined = undefined): DebugConfiguration {
            return {
                name: programPath ? basename(programPath) : "Neo Contract",
                type: "neo-contract",
                request: "launch",
                program: programPath && folder
                    ? slash(join("${workspaceFolder}", relative(folder.uri.fsPath, programPath)))
                    : "${workspaceFolder}/<insert path to contract here>",
                invocation: {
                    operation: (programPath ? extname(programPath) : "") === ".nef"
                    ? "<insert operation here>"
                    : undefined,
                    args: [],
                },
                storage: []
            };
        }

        if (folder)
        {
            var neoVmFiles = await vscode.workspace.findFiles(new vscode.RelativePattern(folder, "**/*.{avm,nef}"));
            if (neoVmFiles.length > 0) {
                return neoVmFiles.map(f => createConfig(f.fsPath));
            }
        }

        return [createConfig()];
    }
}

function getAdapterInfo(program: string) : [string, string] {
    const ext = extname(program);
    switch (ext)
    {
        case '.nef': 
            return ['Neo.Debug3.Adapter', 'neodebug-3-adapter'];
        case '.avm':
            return ['Neo.Debug2.Adapter', 'neodebug-2-adapter'];
        default: 
            throw new Error(`Unexpected Neo contract extension {ext}`);
    }
}

function getDebugAdapterPath(extension:vscode.Extension<any>, packageId: string, assemblyName: string): string {
    const path = resolve(extension.extensionPath, packageId, assemblyName);
    return os.platform() === "win32" ? `${path}.exe` : path;
}

async function getDebugAdapterPackagePath(extension:vscode.Extension<any>, packageId: string) {
    const currentVersionPackageName = resolve(extension.extensionPath, `${packageId}.${extension.packageJSON.version}.nupkg`);
    if (await checkFileExists(currentVersionPackageName)) {
        return currentVersionPackageName;
    }

    var matchGlob = join(extension.extensionPath, `${packageId}.*.nupkg`);
    var matches = await glob(matchGlob);

    if (matches.length === 1) {
        return matches[0];
    }

    return undefined;
}

function getDebugAdapterVersion(path:string, packageId: string) {

    const base = basename(path, ".nupkg");
    if (base.startsWith(`${packageId}.`)) {
        return base.substring(packageId.length + 1);
    }

    throw new Error();
}

async function getDebugAdapterCommand(program: string, config:vscode.WorkspaceConfiguration, mode: vscode.ExtensionMode, channel: vscode.OutputChannel) : Promise<[string, string[]]> {

    const debugAdapterConfig = config.get<string[]>("debug-adapter");
    if (debugAdapterConfig && debugAdapterConfig.length > 0) {
        return [debugAdapterConfig[0], debugAdapterConfig.slice(1)];
    }

    const extension = vscode.extensions.getExtension("ngd-seattle.neo-contract-debug") as vscode.Extension<any>;
    const [packageId, assemblyName] = getAdapterInfo(program);
    const adapterPath = getDebugAdapterPath(extension, packageId, assemblyName);

    if (await checkFileExists(adapterPath)) {
        return [adapterPath, []];
    }

    const adapterPackagePath = await getDebugAdapterPackagePath(extension, packageId);
    if (adapterPackagePath) {
        channel.appendLine(`Installing ${basename(adapterPackagePath)} package`);
        var version = getDebugAdapterVersion(adapterPackagePath, packageId);

        const commandLine = `dotnet tool install ${packageId} --version ${version} --tool-path ./${packageId} --add-source .`;
        channel.appendLine(`  executing \"${commandLine}\"`);
        await execChildProcess(commandLine, extension.extensionPath);
        channel.appendLine(`  deleting \"${basename(adapterPackagePath)}\"`);
        await deleteFile(adapterPackagePath);
        return [adapterPath, []];
    }

    if (mode === vscode.ExtensionMode.Development) {
        const [folderName, framework] = getAdapterProjectProperties(program);
        const adapterProjectFolder = resolve(extension.extensionPath, "..", folderName);

        var adapterProjectPath = resolve(adapterProjectFolder, "bin", "Debug", framework, basename(adapterPath));
        if (await checkFileExists(adapterProjectPath))
        {
            return [adapterProjectPath, []];
        }

        return ["dotnet", ["run", "--project", adapterProjectFolder, "--"]];
    }

    throw new Error("cannot locate debug adapter");

    function getAdapterProjectProperties(program: string) {
        const ext = extname(program);
        switch (ext)
        {
            case '.nef': 
                return ['adapter3', "net5.0"];
            case '.avm':
                return ['adapter2', "netcoreapp3.1"];
            default: 
                throw new Error(`Unexpected Neo contract extension {ext}`);
        }
    }
}

function validateNeo2DebugConfig(config: vscode.DebugConfiguration) {

    if (config["traceFile"]) {
        throw new Error("traceFile configuration not supported in Neo 2");
    }

    if (config["operation"]) {
        throw new Error("operation configuration not supported in Neo 2");
    }
}

function validateNeo3DebugConfig(config: vscode.DebugConfiguration) {

    if (config["utxo"]) {
        throw new Error("utxo configuration not supported in Neo 3");
    }
}

function validateDebugConfig(program: string, config: vscode.DebugConfiguration) {
    const ext = extname(program);
    switch (ext)
    {
        case '.nef': 
            validateNeo3DebugConfig(config);
            break;
        case '.avm':
            validateNeo2DebugConfig(config);
            break;
        default: 
            throw new Error(`Unexpected Neo contract extension {ext}`);
    }
}

class NeoContractDebugAdapterDescriptorFactory implements vscode.DebugAdapterDescriptorFactory {

    channel: vscode.OutputChannel;
    mode: vscode.ExtensionMode;
    constructor (channel: vscode.OutputChannel, mode: vscode.ExtensionMode) {
        this.channel = channel;
        this.mode = mode;
    }


    async createDebugAdapterDescriptor(session: vscode.DebugSession, executable: vscode.DebugAdapterExecutable | undefined): Promise<vscode.DebugAdapterDescriptor> {

        const program: string = session.configuration["program"];
        const config = vscode.workspace.getConfiguration("neo-debugger");
        validateDebugConfig(program, session.configuration);

        let [cmd, args] = await getDebugAdapterCommand(program, config, this.mode, this.channel);
        
        if (config.get<Boolean>("debug", false))
        {
            args.push("--debug");
        }

        if (config.get<Boolean>("log", false))
        {
            args.push("--log");
        }

        const defaultDebugView = config.get<string>("default-debug-view");
        if (defaultDebugView)
        {
            args.push("--debug-view");
            args.push(defaultDebugView);
        }

        const options = session.workspaceFolder ? { cwd: session.workspaceFolder.uri.fsPath } : {};
        this.channel.appendLine(`launching ${cmd} ${args.join(' ')}`);
        this.channel.appendLine(`current directory ${options.cwd ?? 'missing'}`);
        return new vscode.DebugAdapterExecutable(cmd, args, options);
    }
}

// this method is called when your extension is deactivated
export function deactivate() {}
