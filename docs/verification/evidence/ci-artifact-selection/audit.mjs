import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { readFile, readdir, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [runDirectory, workflowFile, globModule, reportFile] = process.argv.slice(2);
if (!reportFile) throw new Error('Usage: node audit.mjs <verification-run-directory> <workflow-file> <glob-module> <report-file>');
const runRoot = path.resolve(runDirectory);
const runParent = path.dirname(runRoot);
const workflow = (await readFile(workflowFile, 'utf8')).replaceAll('\r\n', '\n');
const glob = await import(pathToFileURL(path.resolve(globModule)).href);
const packageInfo = JSON.parse(await readFile(path.resolve(path.dirname(globModule), '../package.json'), 'utf8'));
if (packageInfo.version !== '0.6.1') throw new Error('The pinned upload action uses @actions/glob 0.6.1.');

function uploadStep(name) {
    const section = workflow.split(`      - name: ${name}\n`)[1]?.split(/\n      - /)[0];
    if (!section) throw new Error(`Missing upload step: ${name}`);
    const block = section.match(/^          path: \|\r?\n((?:            [^\r\n]+\r?\n)+)/m);
    const patterns = block
        ? block[1].trim().split(/\r?\n/).map(line => line.trim())
        : [section.match(/^          path: (.+)$/m)?.[1].trim()];
    if (patterns.some(pattern => !pattern?.startsWith('artifacts/verification/'))) throw new Error('Unexpected artifact path.');
    return { condition: section.match(/^        if: (.+)$/m)?.[1].trim(), patterns };
}

async function select(patterns) {
    const boundPatterns = patterns.map(pattern => pattern.replace('artifacts/verification/', runParent.replaceAll('\\', '/') + '/'));
    const matcher = await glob.create(boundPatterns.join('\n'), {
        followSymbolicLinks: true, implicitDescendants: true,
        omitBrokenSymbolicLinks: true, excludeHiddenFiles: true
    });
    const files = [];
    for (const file of await matcher.glob()) {
        const relative = path.relative(runRoot, file);
        if (!relative || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) continue;
        if (!(await stat(file)).isDirectory()) files.push(relative.replaceAll('\\', '/'));
    }
    return [...new Set(files)].sort();
}

async function enumerate(directory, prefix = '') {
    const files = [];
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const relative = prefix + entry.name;
        if (entry.isDirectory()) files.push(...await enumerate(path.join(directory, entry.name), relative + '/'));
        else if (entry.isFile()) files.push(relative);
        else throw new Error(`Unexpected link or device in evidence: ${relative}`);
    }
    return files.sort();
}

async function digest(file) {
    const hash = createHash('sha256');
    for await (const chunk of createReadStream(file)) hash.update(chunk);
    return hash.digest('hex');
}

function assertSame(actual, expected, message) {
    if (JSON.stringify(actual) !== JSON.stringify(expected)) throw new Error(message);
}

const success = uploadStep('Upload verified Windows candidate and reports');
const failure = uploadStep('Upload complete failed verification evidence');
assertSame(success.condition, 'success()', 'Candidate upload must require all previous steps to succeed.');
assertSame(failure.condition, "always() && steps.verification.outcome == 'failure'", 'Failure evidence must depend on verification, not upload outcome.');
if (!/^        id: verification\r?\n        run: \.\/tools\/windows\/verify\.ps1$/m.test(workflow)) throw new Error('Verification step id or command changed.');

const all = await enumerate(runRoot);
const selected = await select(success.patterns);
const diagnostics = await select(failure.patterns);
const windows = all.filter(file => file.startsWith('windows/'));
const reports = all.filter(file => !file.includes('/') && /\.(json|trx|log)$/.test(file));
assertSame(selected, [...windows, ...reports].sort(), 'Candidate selection lost a required file or included run data.');
assertSame(diagnostics, all, 'Failure selection lost an evidence file.');
if (!windows.includes('windows/CozyTown.exe') || !reports.includes('verification.json')) throw new Error('Evidence directory is not a complete Windows verification run.');
const manifest = [];
for (const file of all) {
    const absolute = path.join(runRoot, file);
    manifest.push({ file, bytes: (await stat(absolute)).size, sha256: await digest(absolute), candidate: selected.includes(file) });
}
const bytes = entries => entries.reduce((sum, entry) => sum + entry.bytes, 0);
const kept = manifest.filter(entry => entry.candidate);
const verification = JSON.parse(await readFile(path.join(runRoot, 'verification.json'), 'utf8'));
const report = {
    schemaVersion: 1, passed: true, runId: path.basename(runRoot), sourceCommit: verification.sourceCommit,
    sourceVerificationPassed: verification.passed, graphicalRequested: verification.graphicalRequested,
    workflowSha256: await digest(workflowFile), globPackage: { name: packageInfo.name, version: packageInfo.version },
    success, failure, windowsFiles: windows.length,
    topLevelFiles: { json: reports.filter(file => file.endsWith('.json')).length,
        trx: reports.filter(file => file.endsWith('.trx')).length, log: reports.filter(file => file.endsWith('.log')).length },
    full: { files: manifest.length, uncompressedBytes: bytes(manifest) },
    candidate: { files: kept.length, uncompressedBytes: bytes(kept) },
    excluded: { files: manifest.length - kept.length, uncompressedBytes: bytes(manifest) - bytes(kept) },
    failureSelectionFiles: diagnostics.length, remoteUploadVerified: false,
    manifest
};
await writeFile(reportFile, JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ passed: report.passed, full: report.full, candidate: report.candidate,
    windowsFiles: report.windowsFiles, topLevelFiles: report.topLevelFiles, failureSelectionFiles: report.failureSelectionFiles }));
