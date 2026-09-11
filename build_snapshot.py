"""Verified saved-project snapshots for isolated native compilation."""
import hashlib
import json
from pathlib import Path
import shutil
import stat
import tempfile


# Generated indexes, native outputs and local backups are not compiler inputs.
EXCLUDED_ROOTS=frozenset(('.mcp-backups','Bin','temp','Lucene'))


def prune_recent_projects(snapshot_project=None):
    """Remove temp-snapshot entries from FStudio's recent-projects list.

    The isolated compiler opens each snapshot in FStudio, which records the
    temp path in RecentProjects.xml. After the snapshot is removed those
    entries make FStudio show a missing-project dialog on startup, so both
    the worker startup and the snapshot cleanup call this.
    """
    import os,re
    appdata=os.environ.get('APPDATA')
    if not appdata:return 0
    recent=Path(appdata)/'Flexem'/'FStudio3'/'RecentProjects.xml'
    if not recent.is_file():return 0
    try:
        raw=recent.read_text(encoding='utf-8-sig')
        pattern=re.compile(r'\s*<Project Value="[^"]*(?:fsmcp-[^"\/]*|Temp[\/][^"]*fsmcp[^"\/]*)[^"]*"\s*/>')
        cleaned=pattern.sub('',raw)
        if cleaned!=raw:
            recent.write_text(cleaned,encoding='utf-8-sig')
        return len(pattern.findall(raw))
    except Exception:
        return 0


def input_manifest(directory):
    directory=Path(directory).resolve(strict=True)
    result={}
    def visit(folder):
        for path in sorted(folder.iterdir()):
            if folder==directory and path.name in EXCLUDED_ROOTS:continue
            info=path.lstat()
            if path.is_symlink() or getattr(info,'st_file_attributes',0)&0x400:
                raise ValueError('Project snapshot refuses reparse points: '+str(path))
            if path.is_dir():visit(path)
            elif path.is_file():
                digest=hashlib.sha256()
                with path.open('rb') as stream:
                    for block in iter(lambda:stream.read(1024*1024),b''):digest.update(block)
                result[path.relative_to(directory).as_posix()]={'size':info.st_size,'sha256':digest.hexdigest()}
            else:raise ValueError('Unsupported project entry: '+str(path))
    visit(directory)
    return result


class BuildSnapshot:
    def __init__(self, project, *, workspace, artifact_dir, temp_parent=None):
        source=Path(project).resolve(strict=True)
        workspace=Path(workspace).resolve(strict=True)
        if not source.is_relative_to(workspace) or source.suffix.lower()!='.fsprj':
            raise ValueError('Expected a workspace fsprj file')
        if not source.is_file():raise ValueError('Project is not a file')
        self.source=source
        self.artifact_dir=Path(artifact_dir).resolve()
        self.artifact_dir.mkdir(parents=True,exist_ok=True)
        self.owner=Path(tempfile.mkdtemp(prefix='fsmcp-',dir=temp_parent)).resolve()
        self.directory=self.owner/'p'
        self.project=self.directory/source.name
        self.manifest=None
        try:
            if len(str(self.directory))>90:raise ValueError('FStudio project directory exceeds native 90-character limit')
            before=input_manifest(source.parent)
            self.directory.mkdir()
            for relative in before:
                target=self.directory/relative
                target.parent.mkdir(parents=True,exist_ok=True)
                shutil.copyfile(source.parent/relative,target)
            copied=input_manifest(self.directory)
            after=input_manifest(source.parent)
            if before!=after or copied!=before:
                raise RuntimeError('Project inputs changed during snapshot; build was not started')
            self.manifest=before
            self.write_record('snapshot.json',dict(source=str(source),project=str(self.project),owner=str(self.owner),inputs=before,verified=True))
        except BaseException:
            self.cleanup()
            raise

    def write_record(self,name,value):
        if Path(name).name!=name:raise ValueError('Expected record filename')
        target=self.artifact_dir/name
        temporary=target.with_suffix(target.suffix+'.tmp')
        temporary.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf8')
        temporary.replace(target)

    def source_unchanged(self):
        return input_manifest(self.source.parent)==self.manifest

    def archive_outputs(self):
        """Copy native results to durable artifacts; never overwrite editor files."""
        source=self.directory/'Bin'
        if not source.exists():return dict(directory=None,files={})
        before=input_manifest(source)
        destination=self.artifact_dir/'outputs'
        destination.mkdir(exist_ok=False)
        for relative in before:
            target=destination/relative
            target.parent.mkdir(parents=True,exist_ok=True)
            shutil.copyfile(source/relative,target)
        if input_manifest(source)!=before or input_manifest(destination)!=before:
            raise RuntimeError('Native outputs changed while archiving')
        result=dict(directory=str(destination),files=before)
        self.write_record('outputs.json',result)
        return result

    def cleanup(self):
        # Delete only the exact fresh directory owned by this instance. Never
        # accept a target supplied by a state file or a caller.
        if not self.owner.exists():return
        if self.owner.is_symlink() or self.owner.resolve()!=self.owner:
            raise RuntimeError('Snapshot owner path was replaced; refusing cleanup')
        if self.owner==self.source.parent or self.source.is_relative_to(self.owner):
            raise RuntimeError('Snapshot cleanup would include source project')
        shutil.rmtree(self.owner)
        prune_recent_projects()
