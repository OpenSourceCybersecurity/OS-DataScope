import { useBackend } from '../api/backend';
import LiveMyReviews from './live/LiveMyReviews';
import Reviews from './Reviews';

/** The owner portal. The demo shows the prototype's review screen. */
export default function MyReviews() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveMyReviews /> : <Reviews />;
}
