import { useCallback, useState } from 'react';
import { ClipboardCheck, FolderLock } from 'lucide-react';
import { api, type ReviewSummary } from '../../api/client';
import { Callout, Card, Empty, Meter, PageHeader, Tag, Td, Th, Unc, fmtDate } from '../../ui/kit';
import { ReviewView } from './ReviewView';
import { REVIEW_STATE, ROLE_TEXT, dueText, progress, splitReviews, today } from './reviews';
import { useData } from './useData';

/**
 * The owner portal: the reviews waiting for the signed-in person, and the shares and folders they own. Everything
 * here is scoped by the server to this person; nobody needs a Data Scope role to use it.
 */
export default function LiveMyReviews() {
  const reviews = useData(() => api.myReviews(), []);
  const owned = useData(() => api.myOwned(), []);
  const [openId, setOpenId] = useState<string>();

  if (openId) return <MyReview id={openId} onBack={() => (setOpenId(undefined), void reviews.reload())} />;

  const { todo, done } = splitReviews(reviews.data ?? []);
  return (
    <>
      <PageHeader title="My reviews" sub="Confirm who should still be able to open the files you are responsible for. Your decisions never change anything by themselves; IT acts on them." />
      {reviews.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{reviews.error}</Callout></div>}
      {reviews.data && todo.length === 0 && (
        <Card>
          <Empty icon={<ClipboardCheck size={20} />} title="Nothing to review right now">
            When an administrator starts an access review that includes something you own, it appears here and you get an email.
          </Empty>
        </Card>
      )}
      {todo.length > 0 && <ReviewTable title="Waiting for you" reviews={todo} onOpen={setOpenId} />}
      {done.length > 0 && <div className="mt-4"><ReviewTable title="Finished" reviews={done} onOpen={setOpenId} /></div>}

      {owned.data && owned.data.length > 0 && (
        <div className="mt-4">
          <Card title="What you own" pad={false}>
            <ul className="divide-y divide-line">
              {owned.data.map((o) => (
                <li key={o.folderId} className="px-4 py-2.5 flex flex-wrap items-center gap-2 text-[13.5px]">
                  <FolderLock size={15} className="text-ink-3" />
                  <Unc>{o.resource}</Unc>
                  <Tag>{o.role === 'Owner' ? 'Owner' : 'Backup owner'}</Tag>
                  <span className="text-ink-3 text-[12.5px]">Reviewed {o.reviewFrequency.toLowerCase()}</span>
                </li>
              ))}
            </ul>
          </Card>
        </div>
      )}
    </>
  );
}

function ReviewTable({ title, reviews, onOpen }: { title: string; reviews: ReviewSummary[]; onOpen: (id: string) => void }) {
  return (
    <Card title={title} pad={false}>
      <div className="overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[720px]">
          <thead>
            <tr><Th>Share or folder</Th><Th>Campaign</Th><Th>Due</Th><Th>Progress</Th><Th>Status</Th></tr>
          </thead>
          <tbody>
            {reviews.map((r) => (
              <tr key={r.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => onOpen(r.id)}>
                <Td>
                  <Unc>{r.resource}</Unc>
                  {r.myRole && r.myRole !== 'Owner' && <div className="text-[12px] text-ink-3">{ROLE_TEXT[r.myRole]}</div>}
                </Td>
                <Td>{r.campaignName}</Td>
                <Td className="whitespace-nowrap">
                  {r.state === 'Submitted' || r.campaignState === 'Closed' ? fmtDate(r.dueOn) : <Tag tone={r.overdue ? 'bad' : 'neutral'}>{dueText(r.dueOn, today())}</Tag>}
                </Td>
                <Td className="min-w-[140px]">
                  <Meter value={progress(r)} tone={r.state === 'Submitted' ? 'good' : 'accent'} label="Decisions made" />
                  <div className="text-[12px] text-ink-3 tnum mt-1">{r.decided} of {r.decisionsRequired}</div>
                </Td>
                <Td><Tag tone={REVIEW_STATE[r.state].tone}>{REVIEW_STATE[r.state].label}</Tag></Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function MyReview({ id, onBack }: { id: string; onBack: () => void }) {
  const detail = useData(() => api.myReview(id), [id]);
  const loadUsers = useCallback((itemId: number, search?: string) => api.myReviewItemUsers(id, itemId, search), [id]);
  if (detail.error) return <Callout tone="bad" title="Can't open this review">{detail.error}</Callout>;
  if (!detail.data) return <p className="text-ink-3">Loading…</p>;
  const replace = async (work: Promise<unknown>) => {
    await work;
    await detail.reload();
  };
  return (
    <ReviewView
      detail={detail.data}
      loadUsers={loadUsers}
      onDecide={(decisions) => replace(api.decide(id, decisions))}
      onSubmit={() => replace(api.submitReview(id))}
      onDelegate={(p) => replace(api.delegateReview(id, p?.sid ?? null))}
      peopleSearch={api.people}
      onBack={onBack}
      backLabel="My reviews"
    />
  );
}
