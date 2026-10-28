import type { SvgIconProps } from '@mui/material'
import AccountBalance from '@mui/icons-material/AccountBalanceRounded'
import Bolt from '@mui/icons-material/BoltRounded'
import CardGiftcard from '@mui/icons-material/CardGiftcardRounded'
import DirectionsBus from '@mui/icons-material/DirectionsBusRounded'
import Flight from '@mui/icons-material/FlightRounded'
import Home from '@mui/icons-material/HomeRounded'
import LocalAtm from '@mui/icons-material/LocalAtmRounded'
import LocalGasStation from '@mui/icons-material/LocalGasStationRounded'
import LocalPharmacy from '@mui/icons-material/LocalPharmacyRounded'
import MoreHoriz from '@mui/icons-material/MoreHorizRounded'
import Percent from '@mui/icons-material/PercentRounded'
import Replay from '@mui/icons-material/ReplayRounded'
import Restaurant from '@mui/icons-material/RestaurantRounded'
import Savings from '@mui/icons-material/SavingsRounded'
import School from '@mui/icons-material/SchoolRounded'
import Shield from '@mui/icons-material/ShieldRounded'
import ShoppingBag from '@mui/icons-material/ShoppingBagRounded'
import ShoppingBasket from '@mui/icons-material/ShoppingBasketRounded'
import Spa from '@mui/icons-material/SpaRounded'
import Subscriptions from '@mui/icons-material/SubscriptionsRounded'
import SwapHoriz from '@mui/icons-material/SwapHorizRounded'
import Theaters from '@mui/icons-material/TheatersRounded'
import TrendingUp from '@mui/icons-material/TrendingUpRounded'
import Wifi from '@mui/icons-material/WifiRounded'
import Work from '@mui/icons-material/WorkRounded'
import Receipt from '@mui/icons-material/ReceiptLongRounded'
import CreditCard from '@mui/icons-material/CreditCardRounded'
import CreditScore from '@mui/icons-material/CreditScoreRounded'
import FlightTakeoff from '@mui/icons-material/FlightTakeoffRounded'
import Luggage from '@mui/icons-material/LuggageRounded'
import Redeem from '@mui/icons-material/RedeemRounded'
import Celebration from '@mui/icons-material/CelebrationRounded'
import Chair from '@mui/icons-material/ChairRounded'
import Build from '@mui/icons-material/BuildRounded'
import MedicalServices from '@mui/icons-material/MedicalServicesRounded'
import AutoAwesome from '@mui/icons-material/AutoAwesomeRounded'

const icons: Record<string, React.ComponentType<SvgIconProps>> = {
  AccountBalance, Bolt, CardGiftcard, DirectionsBus, Flight, Home, LocalAtm, LocalGasStation, LocalPharmacy, MoreHoriz,
  Percent, Replay, Restaurant, Savings, School, Shield, ShoppingBag, ShoppingBasket, Spa, Subscriptions, SwapHoriz,
  Theaters, TrendingUp, Wifi, Work, Receipt, CreditCard, CreditScore, FlightTakeoff, Luggage, Redeem, Celebration, Chair, Build,
  MedicalServices, AutoAwesome,
}

export function CategoryIcon({ name, ...props }: { name: string } & SvgIconProps) {
  const Icon = icons[name] ?? MoreHoriz
  return <Icon {...props} />
}
